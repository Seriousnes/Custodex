# M1/04 — Postgres Relation, Schema, Attribute & ChangeLog Stores Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dapper implementations of `IRelationStore`, `ISchemaStore`, `IAttributeStore`, and `IChangeLogStore` against the schema from `m1/01`, **every statement hard-filtering on `store_id + tenant_id`** (spec §6.3). Map `ConditionRef` parameters and attribute dictionaries to/from `jsonb`. Prove tenant isolation with a cross-tenant test (write tenant A, read tenant B, get nothing).

**Architecture:** Each store is a thin Dapper class. Reads open their own short-lived `NpgsqlConnection` from the configured connection string; writes run through the live `NpgsqlConnection`/`NpgsqlTransaction` carried by the `IUnitOfWork` (resolved via `NpgsqlUnitOfWork.From` from `m1/03`), so they commit inside the caller's transaction. A shared `Json` helper centralises jsonb (de)serialization of condition params, attributes, schema definitions, and change-log before/after. `WriteAsync` upserts adds via `ON CONFLICT` on the natural key (`m1/01`) and deletes removes by the same key. `store_id + tenant_id` is a non-optional predicate in every query — defence in depth, not an ambient filter.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Depends on `m0/01` (contracts), `m1/01` (schema, `MigrationRunner`, `PostgresFixture`), `m1/03` (`NpgsqlUnitOfWork`, `NpgsqlUnitOfWorkFactory`). **No EF Core.**

## Shared decisions (locked — used verbatim by m1/07)

- **jsonb helper** `Custodex.Storage.Postgres.Json` exposes `string Serialize(object? value)` and `T? Deserialize<T>(string? json)` over `System.Text.Json` with default options. `jsonb` columns are written as `string` parameters typed `NpgsqlDbType.Jsonb`; null dictionaries serialize to SQL `NULL`.
- **Tuple natural key** (matches `m1/01`'s `ux_relation_tuples_natural`):
  `(store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id, COALESCE(subject_relation, ''))`.
- **Every** read and write filters `store_id` **and** `tenant_id`. There is no overload that omits either.
- Writes use `NpgsqlUnitOfWork.From(uow).Connection` / `.Transaction`; reads open their own connection.

---

### Task 1: The `Json` jsonb helper, polymorphic `PermExpr` converter, and Dapper config

**Files:**
- Create: `src/Custodex.Storage.Postgres/Json.cs`
- Create: `src/Custodex.Storage.Postgres/PermExprJsonConverter.cs`
- Create: `src/Custodex.Storage.Postgres/DapperConfig.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/JsonTests.cs`

**Interfaces:**
- Produces: `static class Json` with `Serialize(object?)` and `Deserialize<T>(string?)`; a `JsonConverterFactory` (`PermExprJsonConverter`, also covering `ConditionExpr`) that round-trips the abstract `PermExpr`/`ConditionExpr` AST via a `$type` discriminator (required because `System.Text.Json` cannot deserialize abstract records by default, and the contract's `PermExpr`/`ConditionExpr` carry no `[JsonPolymorphic]` attributes — see Contract gaps); and a `DapperConfig` module initializer enabling underscore name matching so snake_case columns map to PascalCase record fields. Every store depends on `DapperConfig`.

> **Why the converter and the module initializer are mandatory, not optional:**
> - Dapper's default column→property matching is case-insensitive but **underscore-sensitive**: without `DefaultTypeMap.MatchNamesWithUnderscores = true`, a `subject_type` column never fills a `SubjectType` field (it silently stays `null`/`default`). Every store's `QueryAsync<Row>` over snake_case columns depends on this flag.
> - `System.Text.Json` cannot serialize/deserialize the abstract `PermExpr` hierarchy (`RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned`) or `ConditionExpr` without polymorphism metadata. `NpgsqlSchemaStore` (Task 5) round-trips a whole `Schema` through `jsonb`, so the converter is load-bearing.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/JsonTests.cs
using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

public class JsonTests
{
    [Fact]
    public void Round_trips_a_dictionary()
    {
        var dict = new Dictionary<string, object?> { ["start"] = 8, ["label"] = "am" };
        var json = Json.Serialize(dict);
        var back = Json.Deserialize<Dictionary<string, object?>>(json);
        back.ShouldNotBeNull();
        back!["label"].ShouldBe("am");
    }

    [Fact]
    public void Deserialize_returns_default_for_null()
    {
        Json.Deserialize<Dictionary<string, object?>>(null).ShouldBeNull();
    }

    [Fact]
    public void Round_trips_a_polymorphic_perm_expression()
    {
        PermExpr expr = new Exclude(
            new Union(new RelationRef("medicator"), new Arrow("enclosure", "edit")),
            new RelationRef("blocked"));

        var json = Json.Serialize(expr);
        var back = Json.Deserialize<PermExpr>(json);

        var exclude = back.ShouldBeOfType<Exclude>();
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("medicator");
        union.Right.ShouldBeOfType<Arrow>().Permission.ShouldBe("edit");
    }

    [Fact]
    public void Round_trips_a_full_schema_through_jsonb_text()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [new RelationDef("medicator", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("medicator"))])],
            []);

        var back = Json.Deserialize<Schema>(Json.Serialize(schema));
        back!.Version.ShouldBe("v1");
        back.Types.Single().Permissions.Single().Expression.ShouldBeOfType<RelationRef>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter JsonTests`
Expected: FAIL — `Json` does not exist (and, once it does, the polymorphic cases fail until the converter is registered).

- [ ] **Step 3: Implement the polymorphic converter**

> The factory writes a `$type` discriminator and re-reads it. It covers both abstract bases. Buffering the node into a `JsonObject` keeps the read simple and order-independent.

```csharp
// src/Custodex.Storage.Postgres/PermExprJsonConverter.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Polymorphic (de)serialization for the abstract <see cref="PermExpr"/> and
/// <see cref="ConditionExpr"/> ASTs via a "$type" discriminator. Needed because the contract's
/// records carry no System.Text.Json polymorphism attributes (see Contract gaps in m1/04).
/// </summary>
public sealed class PermExprJsonConverter : JsonConverterFactory
{
    private static readonly IReadOnlyDictionary<string, Type> PermTypes = new Dictionary<string, Type>
    {
        ["relation"] = typeof(RelationRef),
        ["union"] = typeof(Union),
        ["intersect"] = typeof(Intersect),
        ["exclude"] = typeof(Exclude),
        ["arrow"] = typeof(Arrow),
        ["conditioned"] = typeof(Conditioned),
    };

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(PermExpr) || typeToConvert == typeof(ConditionExpr);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(PermExpr))
            return new DiscriminatedConverter<PermExpr>(PermTypes);
        // ConditionExpr concrete nodes are owned by m0/06; until they land the map is empty and
        // serialization of a non-null body throws a clear error rather than silently dropping it.
        return new DiscriminatedConverter<ConditionExpr>(new Dictionary<string, Type>());
    }

    private sealed class DiscriminatedConverter<TBase>(IReadOnlyDictionary<string, Type> byTag)
        : JsonConverter<TBase> where TBase : class
    {
        private readonly Dictionary<Type, string> _byType =
            byTag.ToDictionary(kv => kv.Value, kv => kv.Key);

        public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader)?.AsObject()
                ?? throw new JsonException("Expected a JSON object for a discriminated AST node.");
            var tag = node["$type"]?.GetValue<string>()
                ?? throw new JsonException("Discriminated AST node is missing '$type'.");
            if (!byTag.TryGetValue(tag, out var concrete))
                throw new JsonException($"Unknown {typeof(TBase).Name} '$type' '{tag}'.");
            node.Remove("$type");
            return (TBase?)node.Deserialize(concrete, StripSelf(options));
        }

        public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
        {
            var concrete = value.GetType();
            if (!_byType.TryGetValue(concrete, out var tag))
                throw new JsonException($"Cannot serialize {typeof(TBase).Name} of runtime type {concrete.Name}.");
            var node = JsonSerializer.SerializeToNode(value, concrete, StripSelf(options))!.AsObject();
            node["$type"] = tag;
            node.WriteTo(writer, options);
        }

        // Avoid infinite recursion: serialize the concrete type without this factory in scope.
        private static JsonSerializerOptions StripSelf(JsonSerializerOptions options)
        {
            var clone = new JsonSerializerOptions(options);
            for (var i = clone.Converters.Count - 1; i >= 0; i--)
                if (clone.Converters[i] is PermExprJsonConverter)
                    clone.Converters.RemoveAt(i);
            return clone;
        }
    }
}
```

- [ ] **Step 4: Implement the `Json` helper wired to the converter**

```csharp
// src/Custodex.Storage.Postgres/Json.cs
using System.Text.Json;

namespace Custodex.Storage.Postgres;

public static class Json
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PermExprJsonConverter());
        return options;
    }

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) =>
        json is null ? default : JsonSerializer.Deserialize<T>(json, Options);
}
```

- [ ] **Step 5: Implement the Dapper module initializer**

```csharp
// src/Custodex.Storage.Postgres/DapperConfig.cs
using System.Runtime.CompilerServices;

namespace Custodex.Storage.Postgres;

internal static class DapperConfig
{
    // Dapper matches column names case-insensitively but underscore-sensitively. Enabling underscore
    // matching lets snake_case columns (object_type, occurred_at, …) fill PascalCase record fields.
    [ModuleInitializer]
    internal static void Init() => Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
}
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter JsonTests`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add jsonb helper, polymorphic PermExpr converter, and Dapper underscore matching"
```

---

### Task 2: `NpgsqlRelationStore` — write, get-by-object, get-by-subject

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlRelationStore.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/RelationStoreTests.cs`

**Interfaces:**
- Produces: `NpgsqlRelationStore : IRelationStore` with `GetByObjectAsync`, `GetBySubjectAsync`, `WriteAsync(add, remove, uow)`. Constructor takes the connection string for reads; writes use the supplied `IUnitOfWork`. Maps `condition_name`/`condition_params` ⇄ `ConditionRef`.

- [ ] **Step 1: Write the failing tests** (round-trip a conditioned tuple; delete; subject lookup)

```csharp
// tests/Custodex.Storage.Postgres.Tests/RelationStoreTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class RelationStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Write_then_get_by_object_round_trips_a_conditioned_tuple()
    {
        var t = new TenantContext("zoo", "tA");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var tuple = new RelationTuple(
            new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours",
                new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }

        var found = await store.GetByObjectAsync(t, new EntityRef("category", "drugs"), "dispenser");
        var got = found.ShouldHaveSingleItem();
        got.Subject.ShouldBe(new SubjectRef("group", "vets", "member"));
        got.Condition.ShouldNotBeNull();
        got.Condition!.Name.ShouldBe("within_hours");
        got.Condition.Parameters["start"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Write_remove_deletes_by_natural_key()
    {
        var t = new TenantContext("zoo", "tDel");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var tuple = new RelationTuple(
            new EntityRef("animal", "EL-1"), "medicator", new SubjectRef("user", "alice"));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [], [tuple], u);
            await u.CommitAsync();
        }

        (await store.GetByObjectAsync(t, new EntityRef("animal", "EL-1"), "medicator")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_by_subject_returns_tuples_for_that_subject()
    {
        var t = new TenantContext("zoo", "tSub");
        await SeedTenantAsync(t);
        var store = new NpgsqlRelationStore(fx.ConnectionString);

        var tuple = new RelationTuple(
            new EntityRef("animal", "EL-9"), "medicator", new SubjectRef("user", "carol"));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }

        var bySubject = await store.GetBySubjectAsync(t, new SubjectRef("user", "carol"));
        bySubject.ShouldHaveSingleItem().Object.ShouldBe(new EntityRef("animal", "EL-9"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter RelationStoreTests`
Expected: FAIL — `NpgsqlRelationStore` does not exist.

- [ ] **Step 3: Implement the relation store**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlRelationStore.cs
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

public sealed class NpgsqlRelationStore(string connectionString) : IRelationStore
{
    private sealed record Row(
        string ObjectType, string ObjectId, string Relation,
        string SubjectType, string SubjectId, string? SubjectRelation,
        string? ConditionName, string? ConditionParams);

    private const string SelectColumns =
        "object_type, object_id, relation, subject_type, subject_id, subject_relation, " +
        "condition_name, condition_params::text AS condition_params";

    public async Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(
        TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = @ot AND object_id = @oid AND relation = @rel
            """,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(
        TenantContext t, SubjectRef subject, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {SelectColumns} FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND subject_type = @st AND subject_id = @sid
              AND COALESCE(subject_relation, '') = COALESCE(@srel, '')
            """,
            new { store = t.Store, tenant = t.Tenant, st = subject.Type, sid = subject.Id, srel = subject.Relation },
            cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task WriteAsync(
        TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);

        foreach (var tuple in remove)
        {
            await using var cmd = new NpgsqlCommand("""
                DELETE FROM relation_tuples
                WHERE store_id = @store AND tenant_id = @tenant
                  AND object_type = @ot AND object_id = @oid AND relation = @rel
                  AND subject_type = @st AND subject_id = @sid
                  AND COALESCE(subject_relation, '') = COALESCE(@srel, '')
                """, w.Connection, w.Transaction);
            AddKeyParams(cmd, t, tuple);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var tuple in add)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO relation_tuples
                    (store_id, tenant_id, object_type, object_id, relation,
                     subject_type, subject_id, subject_relation, condition_name, condition_params)
                VALUES (@store, @tenant, @ot, @oid, @rel, @st, @sid, @srel, @cname, @cparams)
                ON CONFLICT (store_id, tenant_id, object_type, object_id, relation,
                             subject_type, subject_id, COALESCE(subject_relation, ''))
                DO UPDATE SET condition_name = EXCLUDED.condition_name,
                              condition_params = EXCLUDED.condition_params
                """, w.Connection, w.Transaction);
            AddKeyParams(cmd, t, tuple);
            cmd.Parameters.AddWithValue("cname", (object?)tuple.Condition?.Name ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("cparams", NpgsqlDbType.Jsonb)
            {
                Value = tuple.Condition is null ? DBNull.Value : Json.Serialize(tuple.Condition.Parameters)
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AddKeyParams(NpgsqlCommand cmd, TenantContext t, RelationTuple tuple)
    {
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("ot", tuple.Object.Type);
        cmd.Parameters.AddWithValue("oid", tuple.Object.Id);
        cmd.Parameters.AddWithValue("rel", tuple.Relation);
        cmd.Parameters.AddWithValue("st", tuple.Subject.Type);
        cmd.Parameters.AddWithValue("sid", tuple.Subject.Id);
        cmd.Parameters.AddWithValue("srel", (object?)tuple.Subject.Relation ?? DBNull.Value);
    }

    private static RelationTuple Map(Row r)
    {
        ConditionRef? condition = r.ConditionName is null
            ? null
            : new ConditionRef(r.ConditionName,
                Json.Deserialize<Dictionary<string, object?>>(r.ConditionParams)
                    ?? new Dictionary<string, object?>());

        return new RelationTuple(
            new EntityRef(r.ObjectType, r.ObjectId),
            r.Relation,
            new SubjectRef(r.SubjectType, r.SubjectId, r.SubjectRelation),
            condition);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter RelationStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add Dapper relation store with condition jsonb mapping"
```

---

### Task 3: Cross-tenant isolation test for the relation store

**Files:**
- Test: `tests/Custodex.Storage.Postgres.Tests/CrossTenantIsolationTests.cs`

**Interfaces:**
- Consumes: `NpgsqlRelationStore`. Proves that writing into tenant A and reading from tenant B (same store) returns nothing — the `store_id + tenant_id` hard filter at work.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/CrossTenantIsolationTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class CrossTenantIsolationTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Tuple_written_in_tenant_A_is_invisible_in_tenant_B()
    {
        var a = new TenantContext("zoo", "tenant-A");
        var b = new TenantContext("zoo", "tenant-B");
        await SeedTenantAsync(a);
        await SeedTenantAsync(b);

        var store = new NpgsqlRelationStore(fx.ConnectionString);
        var tuple = new RelationTuple(
            new EntityRef("animal", "EL-001"), "medicator", new SubjectRef("user", "dr-smith"));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(a, [tuple], [], u);
            await u.CommitAsync();
        }

        // Same object key, different tenant → nothing, by object and by subject.
        (await store.GetByObjectAsync(b, new EntityRef("animal", "EL-001"), "medicator")).ShouldBeEmpty();
        (await store.GetBySubjectAsync(b, new SubjectRef("user", "dr-smith"))).ShouldBeEmpty();

        // Tenant A still sees its own tuple.
        (await store.GetByObjectAsync(a, new EntityRef("animal", "EL-001"), "medicator"))
            .ShouldHaveSingleItem();
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CrossTenantIsolationTests`
Expected: PASS — the `tenant_id` predicate isolates the read. (If it fails, a query is missing the tenant filter; fix the store, not the test.)

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Storage.Postgres.Tests
git commit -m "test: prove cross-tenant isolation on the relation store"
```

---

### Task 4: `NpgsqlAttributeStore` — attribute dictionary ⇄ jsonb

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlAttributeStore.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/AttributeStoreTests.cs`

**Interfaces:**
- Produces: `NpgsqlAttributeStore : IAttributeStore` with `GetAsync` (null when absent) and `SetAsync(uow)` (upsert on the object PK), mapping the attribute dictionary to/from the `attributes` jsonb column. Hard-filters `store_id + tenant_id`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/AttributeStoreTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class AttributeStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Set_then_get_round_trips_attributes()
    {
        var t = new TenantContext("zoo", "tAttr");
        await SeedTenantAsync(t);
        var store = new NpgsqlAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("animal", "EL-001");

        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj,
                new Dictionary<string, object?> { ["is_quarantine"] = true, ["weight_kg"] = 5400 }, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs.ShouldNotBeNull();
        attrs!["is_quarantine"].ShouldNotBeNull();
        attrs.ContainsKey("weight_kg").ShouldBeTrue();
    }

    [Fact]
    public async Task Set_upserts_replacing_prior_attributes()
    {
        var t = new TenantContext("zoo", "tAttr2");
        await SeedTenantAsync(t);
        var store = new NpgsqlAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("animal", "EL-002");

        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?> { ["v"] = 1 }, u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?> { ["v"] = 2 }, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs!["v"]!.ToString().ShouldBe("2");
    }

    [Fact]
    public async Task Get_returns_null_when_absent()
    {
        var t = new TenantContext("zoo", "tAttr3");
        await SeedTenantAsync(t);
        var store = new NpgsqlAttributeStore(fx.ConnectionString);
        (await store.GetAsync(t, new EntityRef("animal", "missing"))).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter AttributeStoreTests`
Expected: FAIL — `NpgsqlAttributeStore` does not exist.

- [ ] **Step 3: Implement the attribute store**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlAttributeStore.cs
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

public sealed class NpgsqlAttributeStore(string connectionString) : IAttributeStore
{
    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        TenantContext t, EntityRef obj, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT attributes::text FROM object_attributes
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = @ot AND object_id = @oid
            """,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id },
            cancellationToken: ct));

        return json is null ? null : Json.Deserialize<Dictionary<string, object?>>(json);
    }

    public async Task SetAsync(
        TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO object_attributes (store_id, tenant_id, object_type, object_id, attributes)
            VALUES (@store, @tenant, @ot, @oid, @attrs)
            ON CONFLICT (store_id, tenant_id, object_type, object_id)
            DO UPDATE SET attributes = EXCLUDED.attributes
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("ot", obj.Type);
        cmd.Parameters.AddWithValue("oid", obj.Id);
        cmd.Parameters.Add(new NpgsqlParameter("attrs", NpgsqlDbType.Jsonb) { Value = Json.Serialize(attrs) });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter AttributeStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add Dapper attribute store with jsonb mapping"
```

---

### Task 5: `NpgsqlSchemaStore` — active schema persistence

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlSchemaStore.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/SchemaStoreTests.cs`

**Interfaces:**
- Produces: `NpgsqlSchemaStore : ISchemaStore` with `GetActiveAsync(store)` and `SetActiveAsync(store, schema, uow)`. The `Schema` AST is serialized to the `definition` jsonb column; setting active deactivates any previously active version for that store, then upserts the new one as active. Schema is per-store (not tenant-scoped), matching the contract signatures.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/SchemaStoreTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class SchemaStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedStoreAsync(string store)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = store }, uow.Transaction);
        await u.CommitAsync();
    }

    private static Schema SchemaV(string version) => new(version,
        [new EntityTypeDef("animal",
            [new RelationDef("medicator", [new SubjectTypeRef("user")])],
            [new PermissionDef("edit", new RelationRef("medicator"))])],
        []);

    [Fact]
    public async Task Set_active_then_get_active_round_trips()
    {
        const string store = "schema-store";
        await SeedStoreAsync(store);
        var schemaStore = new NpgsqlSchemaStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await schemaStore.SetActiveAsync(store, SchemaV("v1"), u);
            await u.CommitAsync();
        }

        var active = await schemaStore.GetActiveAsync(store);
        active.ShouldNotBeNull();
        active!.Version.ShouldBe("v1");
        active.Types.Single().Name.ShouldBe("animal");
    }

    [Fact]
    public async Task Set_active_a_second_version_supersedes_the_first()
    {
        const string store = "schema-store-2";
        await SeedStoreAsync(store);
        var schemaStore = new NpgsqlSchemaStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await schemaStore.SetActiveAsync(store, SchemaV("v1"), u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await schemaStore.SetActiveAsync(store, SchemaV("v2"), u);
            await u.CommitAsync();
        }

        (await schemaStore.GetActiveAsync(store))!.Version.ShouldBe("v2");
    }

    [Fact]
    public async Task Get_active_returns_null_for_unknown_store()
    {
        var schemaStore = new NpgsqlSchemaStore(fx.ConnectionString);
        (await schemaStore.GetActiveAsync("never-set")).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter SchemaStoreTests`
Expected: FAIL — `NpgsqlSchemaStore` does not exist.

- [ ] **Step 3: Implement the schema store**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlSchemaStore.cs
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

public sealed class NpgsqlSchemaStore(string connectionString) : ISchemaStore
{
    public async Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT definition::text FROM schema_versions
            WHERE store_id = @store AND is_active
            """,
            new { store }, cancellationToken: ct));

        return json is null ? null : Json.Deserialize<Schema>(json);
    }

    public async Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);

        await using (var deactivate = new NpgsqlCommand(
            "UPDATE schema_versions SET is_active = false WHERE store_id = @store AND is_active",
            w.Connection, w.Transaction))
        {
            deactivate.Parameters.AddWithValue("store", store);
            await deactivate.ExecuteNonQueryAsync(ct);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO schema_versions (store_id, version, definition, is_active)
            VALUES (@store, @version, @definition, true)
            ON CONFLICT (store_id, version)
            DO UPDATE SET definition = EXCLUDED.definition, is_active = true
            """, w.Connection, w.Transaction);
        upsert.Parameters.AddWithValue("store", store);
        upsert.Parameters.AddWithValue("version", schema.Version);
        upsert.Parameters.Add(new NpgsqlParameter("definition", NpgsqlDbType.Jsonb) { Value = Json.Serialize(schema) });
        await upsert.ExecuteNonQueryAsync(ct);
    }
}
```

> The `Schema` AST contains the `PermExpr` polymorphic hierarchy, which `System.Text.Json` cannot round-trip by default. The `PermExprJsonConverter` registered into the `Json` helper in Task 1 handles this via a `$type` discriminator, so `SetActiveAsync`/`GetActiveAsync` round-trip the schema through `jsonb` here. The underlying need for `[JsonPolymorphic]`/`[JsonDerivedType]` on the contract's `PermExpr`/`ConditionExpr` records is a Contract gap (m0's conformance/DSL serialization hits the same wall); the converter is the provider-side workaround until those attributes land in `Custodex.Abstractions`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter SchemaStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add Dapper schema store with active-version upsert"
```

---

### Task 6: `NpgsqlChangeLogStore` — append-only audit read/write

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlChangeLogStore.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/ChangeLogStoreTests.cs`

**Interfaces:**
- Produces: `NpgsqlChangeLogStore : IChangeLogStore` with `AppendAsync(uow)` (insert; DB generates `id` via `bigserial` and `occurred_at` via `default now()`, so the passed `Id`/`OccurredAt` are ignored) and `ReadAsync(filter)` (newest-first, filtered by `Since`/`Actor`, capped at `Limit`). Maps `before`/`after` to/from `jsonb`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/ChangeLogStoreTests.cs
using Custodex.Abstractions;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class ChangeLogStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    private static ChangeLogEntry Entry(string actor, string op, string target, object? before, object? after) =>
        new(0, actor, op, target, before, after, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Append_then_read_returns_the_entry_with_db_generated_id()
    {
        var t = new TenantContext("zoo", "tLog");
        await SeedTenantAsync(t);
        var store = new NpgsqlChangeLogStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await store.AppendAsync(t, Entry("dr-admin", "write",
                "category:drugs#dispenser@group:vets#member", null, new { granted = true }), u);
            await u.CommitAsync();
        }

        var entries = await store.ReadAsync(t, new ChangeLogFilter());
        var e = entries.ShouldHaveSingleItem();
        e.Id.ShouldBeGreaterThan(0);          // DB-generated bigserial
        e.Actor.ShouldBe("dr-admin");
        e.Operation.ShouldBe("write");
        e.OccurredAt.ShouldNotBe(DateTimeOffset.UnixEpoch);   // DB default now(), not the passed value
    }

    [Fact]
    public async Task Read_filters_by_actor_and_respects_limit_newest_first()
    {
        var t = new TenantContext("zoo", "tLog2");
        await SeedTenantAsync(t);
        var store = new NpgsqlChangeLogStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await store.AppendAsync(t, Entry("alice", "write", "a#r@u", null, null), u);
            await store.AppendAsync(t, Entry("bob", "delete", "b#r@u", null, null), u);
            await store.AppendAsync(t, Entry("alice", "write", "c#r@u", null, null), u);
            await u.CommitAsync();
        }

        var aliceOnly = await store.ReadAsync(t, new ChangeLogFilter(Actor: "alice"));
        aliceOnly.Count.ShouldBe(2);
        aliceOnly.All(e => e.Actor == "alice").ShouldBeTrue();

        var capped = await store.ReadAsync(t, new ChangeLogFilter(Limit: 1));
        capped.Count.ShouldBe(1);
        capped[0].Target.ShouldBe("c#r@u");   // newest first
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter ChangeLogStoreTests`
Expected: FAIL — `NpgsqlChangeLogStore` does not exist.

- [ ] **Step 3: Implement the change-log store**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlChangeLogStore.cs
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

public sealed class NpgsqlChangeLogStore(string connectionString) : IChangeLogStore
{
    private sealed record Row(
        long Id, string Actor, string Operation, string Target,
        string? Before, string? After, DateTimeOffset OccurredAt);

    public async Task AppendAsync(
        TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO change_log (store_id, tenant_id, actor, operation, target, before, after)
            VALUES (@store, @tenant, @actor, @operation, @target, @before, @after)
            """, w.Connection, w.Transaction);
        cmd.Parameters.AddWithValue("store", t.Store);
        cmd.Parameters.AddWithValue("tenant", t.Tenant);
        cmd.Parameters.AddWithValue("actor", entry.Actor);
        cmd.Parameters.AddWithValue("operation", entry.Operation);
        cmd.Parameters.AddWithValue("target", entry.Target);
        cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb)
            { Value = entry.Before is null ? DBNull.Value : Json.Serialize(entry.Before) });
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb)
            { Value = entry.After is null ? DBNull.Value : Json.Serialize(entry.After) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(
        TenantContext t, ChangeLogFilter filter, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT id, actor, operation, target, before::text AS before, after::text AS after, occurred_at
            FROM change_log
            WHERE store_id = @store AND tenant_id = @tenant
              AND (@since IS NULL OR occurred_at >= @since)
              AND (@actor IS NULL OR actor = @actor)
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """,
            new { store = t.Store, tenant = t.Tenant, since = filter.Since, actor = filter.Actor, limit = filter.Limit },
            cancellationToken: ct));

        return rows.Select(r => new ChangeLogEntry(
            r.Id, r.Actor, r.Operation, r.Target,
            Json.Deserialize<object?>(r.Before), Json.Deserialize<object?>(r.After), r.OccurredAt)).ToList();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter ChangeLogStoreTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add Dapper change-log store with jsonb before/after"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Every read and write in every store filters on both `store_id` and `tenant_id` (schema store filters `store_id`; schema is per-store).
- [ ] The cross-tenant isolation test passes: tenant A's tuple is invisible from tenant B by object and by subject.
- [ ] Condition params and attribute dictionaries round-trip through `jsonb` via the shared `Json` helper.
- [ ] `WriteAsync` upserts adds on the natural key and deletes removes by the same key; writes flow through the `IUnitOfWork`.
- [ ] Change-log `id`/`occurred_at` are DB-generated; `ReadAsync` returns newest-first, filtered, capped.
- [ ] `DapperConfig.MatchNamesWithUnderscores` is enabled (Task 1) — without it every snake_case column silently maps to `null`/`default` and the round-trip tests fail.
- [ ] `PermExprJsonConverter` is registered in `Json` (Task 1) so the schema store round-trips the `PermExpr` AST through `jsonb`.
