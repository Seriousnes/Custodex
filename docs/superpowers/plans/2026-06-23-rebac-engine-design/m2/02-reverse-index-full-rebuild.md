# M2/02 — Reverse-Index Full Rebuild Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `ReverseIndexRebuilder` — a full rebuild of `reverse_index` for a (store, tenant) from the current tuples and the active schema, expanding every `(subject, permission, object)` **structural** grant via the decided seam (reachability in SQL, algebra in C#) and stamping each row with the active `schema_version` and a `conditioned` flag. This rebuild is the **always-correct safety net** and the **maintenance oracle** the incremental path (`m2/03`) is diffed against (`m2/06`).

**Architecture:** The rebuild computes the same structural truth the `m0/07` `EngineDrivenAuthorizer` ListObjects oracle computes, then **stores it** as `reverse_index` rows. For each `(objectType, permission)` declared in the schema, and each candidate subject (the concrete users and the public `user:*`), it asks the **unconditioned** pointwise Check whether the grant holds; when it does, it writes a row whose `conditioned` flag records whether any condition was reached on the grant path. "Unconditioned" is achieved by evaluating with `NullConditionEvaluator` (every condition is treated as satisfied), so the stored row reflects the *structural* grant; the `ConditionTouched` latch on the `EvalContext` (`m0/05`) tells us whether to flag it. ListObjects (`m2/04`) re-checks only flagged rows at query time, so request-time predicates stay correct (spec §7.3).

The rebuild runs through the **oracle authorizer** (`EngineDrivenAuthorizer`), not the CTE path: the rebuild's job is to be obviously, durably correct (it is the safety net), and the oracle is the project's ground truth (spec §7.4). It is heavier than incremental maintenance, which is the point — correctness over speed; the incremental path (`m2/03`) is the fast path that must agree with this.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Relkit.Core` (`EngineDrivenAuthorizer`, `SchemaIndex`, `EvalContext`, `EvaluationOptions`, `NullConditionEvaluator`).

## Global Constraints

See `../README.md` → Global Constraints. Key points: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. Depends on `m0/05` (`EngineDrivenAuthorizer`, `EvalContext.ConditionTouched`), `m0/07` (the ListObjects oracle semantics this materializes, `CandidateObjectsAsync`/`UniverseOfTypeAsync` shape), `m1/01` (`reverse_index`, `MigrationRunner`, `PostgresFixture`), `m1/03`/`m1/04` (`NpgsqlUnitOfWork*`, `NpgsqlRelationStore`/`SchemaStore`/`AttributeStore`), `m2/01` (`IIndexStore`/`NpgsqlIndexStore`, `ReverseIndexRow`).

> **CALIBRATION.** The rebuild is the *safety net*, so it is written to be correct by construction: it materializes the oracle's already-proven structural truth. The **tests are the spec** — every rebuild test asserts the stored rows equal what the oracle's `ListObjects`/`Check` says structurally. The rebuild is the **maintenance oracle**: `m2/03`'s incremental maintenance is correct iff it produces the same rows this rebuild does, proven by the `m2/06` differential harness. Keep this path simple and obviously-correct even where slow.

---

### Task 1: Expose the structural-grant probe on the oracle (`ConditionTouched` per check)

**Files:**
- Create: `src/Relkit.Core/Evaluation/EngineDrivenAuthorizer.Structural.cs`
- Test: `tests/Relkit.Core.Tests/Evaluation/StructuralProbeTests.cs`

**Interfaces:**
- Produces: a **public** probe on `EngineDrivenAuthorizer` (public because `Relkit.Storage.Postgres`, a separate assembly, calls it from the rebuilder — the same reason `SchemaIndex`/`EvalContext` are public in `Relkit.Core.Evaluation`):
  `Task<StructuralGrant> CheckStructuralAsync(SchemaIndex index, TenantContext tenant, EntityRef obj, string permission, SubjectRef subject, RequestContext context, CancellationToken ct)` returning `public sealed record StructuralGrant(bool Granted, bool Conditioned)`. It runs the pointwise Check under a fresh `EvalContext`, **always with conditions treated as satisfied** (the authorizer is constructed with `NullConditionEvaluator` for rebuild), and reports `Granted` plus whether `EvalContext.ConditionTouched` latched (`Conditioned`).
- Consumes: `CheckPermissionAsync` (private, `m0/05`), `EvalContext`, `SchemaIndex` (`m0/05`).

> **Why a dedicated probe.** The public `CheckAsync` returns only `bool` and loads the schema itself. The rebuilder already holds a `SchemaIndex` and needs the `Conditioned` bit per grant, so it calls this lower-level probe directly. Constructing the authorizer with `NullConditionEvaluator` makes every condition pass — so a grant that exists *only when a condition holds* is recorded as a **structural** grant flagged `conditioned = true`, and `m2/04` re-checks it at query time. A grant with no condition on its path is `conditioned = false` and is honoured directly.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/Evaluation/StructuralProbeTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Evaluation;

public class StructuralProbeTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<(EngineDrivenAuthorizer Auth, SchemaIndex Index)> NewAsync(
        Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Length > 0) await relations.WriteAsync(T, tuples, [], uow);
        await uow.CommitAsync();
        // Rebuild always uses NullConditionEvaluator so conditions are structural-only.
        var auth = new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
        return (auth, new SchemaIndex(schema));
    }

    private static RequestContext Ctx() =>
        new(DateTimeOffset.UnixEpoch, new SubjectRef("user", "<rebuild>"), new Dictionary<string, object?>());

    [Fact]
    public async Task Unconditioned_grant_is_granted_and_not_flagged()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))).Build();
        var (auth, index) = await NewAsync(schema,
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));

        var g = await auth.CheckStructuralForTest(index, T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"), Ctx());
        g.Granted.ShouldBeTrue();
        g.Conditioned.ShouldBeFalse();
    }

    [Fact]
    public async Task Tuple_carrying_a_condition_is_granted_structurally_and_flagged()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();
        // viewer@alice WITH within_hours — structurally a grant; conditioned must flag true.
        var conditioned = new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));
        var (auth, index) = await NewAsync(schema, conditioned);

        var g = await auth.CheckStructuralForTest(index, T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"), Ctx());
        g.Granted.ShouldBeTrue();    // NullConditionEvaluator => condition treated satisfied => structural grant
        g.Conditioned.ShouldBeTrue(); // but the path touched a condition => flag for query-time re-check
    }

    [Fact]
    public async Task Absent_grant_is_not_granted()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))).Build();
        var (auth, index) = await NewAsync(schema);

        var g = await auth.CheckStructuralForTest(index, T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "ghost"), Ctx());
        g.Granted.ShouldBeFalse();
    }
}
```

> The tests call an `internal` test seam `CheckStructuralForTest` forwarding to the real method (the `[InternalsVisibleTo("Relkit.Core.Tests")]` from `m0/07` Task 1 makes internals visible).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter StructuralProbeTests`
Expected: FAIL — `CheckStructuralForTest`/`CheckStructuralAsync`/`StructuralGrant` not defined.

- [ ] **Step 3: Implement the probe**

```csharp
// src/Relkit.Core/Evaluation/EngineDrivenAuthorizer.Structural.cs
using Relkit.Abstractions;

namespace Relkit.Core.Evaluation;

/// <summary>One probed grant: whether it holds structurally, and whether any condition was reached.</summary>
public sealed record StructuralGrant(bool Granted, bool Conditioned);

public sealed partial class EngineDrivenAuthorizer
{
    internal Task<StructuralGrant> CheckStructuralForTest(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission, SubjectRef subject,
        RequestContext context, CancellationToken ct = default)
        => CheckStructuralAsync(index, tenant, obj, permission, subject, context, ct);

    /// <summary>
    /// Probes whether <paramref name="subject"/> holds <paramref name="permission"/> on
    /// <paramref name="obj"/> structurally, reporting whether the grant path touched a condition.
    /// Intended to be called on an authorizer constructed with <c>NullConditionEvaluator</c> (rebuild),
    /// so conditions are treated as satisfied and the result is the structural grant; the latched
    /// <see cref="EvalContext.ConditionTouched"/> becomes the row's <c>conditioned</c> flag.
    /// Public because the reverse-index rebuild (Relkit.Storage.Postgres, m2/02) calls it across assembly.
    /// </summary>
    public async Task<StructuralGrant> CheckStructuralAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission, SubjectRef subject,
        RequestContext context, CancellationToken ct)
    {
        var ctx = new EvalContext(_options);
        var granted = await CheckPermissionAsync(index, tenant, obj, permission, subject, context, ctx, explain: null, ct);
        return new StructuralGrant(granted, granted && ctx.ConditionTouched);
    }
}
```

> `ConditionTouched` is only meaningful for a granted path: a denied check may have short-circuited before reaching a condition, and we never store rows for denials, so `Conditioned` is reported only when `granted`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter StructuralProbeTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core/Evaluation/EngineDrivenAuthorizer.Structural.cs tests/Relkit.Core.Tests/Evaluation/StructuralProbeTests.cs
git commit -m "feat: add structural-grant probe with conditioned flag to oracle"
```

---

### Task 2: Enumerate the rebuild work — subjects, objects, permissions

**Files:**
- Create: `src/Relkit.Storage.Postgres/Index/RebuildEnumeration.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/RebuildEnumerationTests.cs`

**Interfaces:**
- Produces: `RebuildEnumeration` static helpers reading the tenant's tuples once and projecting the rebuild domain:
  - `Task<RebuildInputs> LoadAsync(NpgsqlConnection conn, TenantContext t, CancellationToken ct)` returning `record RebuildInputs(IReadOnlyList<string> Users, IReadOnlyDictionary<string, IReadOnlyList<string>> ObjectIdsByType)` — every concrete `user` id that appears as a subject anywhere (the candidate query subjects), and every object id grouped by object type (the candidate objects). `user:*` is handled by the rebuilder adding the synthetic `*` subject; it is not in `Users`.
  - the rebuilder pairs these with the schema's `(type, permission)` declarations to form the full cross-product of grants to probe.
- Consumes: `relation_tuples` (`m1/01`); `TenantContext`.

> **Domain of the rebuild.** A reverse-index row is `(subject, permission, object_type, object_id)`. The candidate **subjects** are the concrete users in the tenant plus the public `user:*` (other subject types are never the query subject of a `ListObjects`). The candidate **objects** are all objects of each type that appear in any tuple (the type universe, exactly as `m0/07 UniverseOfTypeAsync`/`m1/06 TypeUniverseAsync`). The candidate **permissions** come from the schema. Probing each `(user, permission, object)` with the structural Check and keeping the granted ones is the rebuild. This is O(users × objects × permissions) Checks — acceptable at the spec's target scale (dozens-to-hundreds of users, low-thousands of resources) and acceptable because it is the safety net, not the hot path.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/RebuildEnumerationTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class RebuildEnumerationTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("rebuild-enum", "t");

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("species", "kangaroo"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("species", "wallaby"), "editor", new SubjectRef("user", "*")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "alice")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "bob")),
        ], [], u);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Load_projects_concrete_users_and_objects_by_type()
    {
        await using var conn = await fx.OpenAsync();
        var inputs = await RebuildEnumeration.LoadAsync(conn, T);

        inputs.Users.OrderBy(x => x).ShouldBe(["alice", "bob"]);   // concrete users only; "*" excluded
        inputs.ObjectIdsByType["species"].OrderBy(x => x).ShouldBe(["kangaroo", "wallaby"]);
        inputs.ObjectIdsByType["group"].ShouldBe(["macropods"]);   // groups appear too; schema gates which get probed
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter RebuildEnumerationTests`
Expected: FAIL — `RebuildEnumeration` does not exist.

- [ ] **Step 3: Implement the enumeration**

```csharp
// src/Relkit.Storage.Postgres/Index/RebuildEnumeration.cs
using Dapper;
using Npgsql;
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Index;

/// <summary>
/// Projects the rebuild domain from a tenant's tuples: the concrete user subjects and the objects
/// grouped by type. The rebuilder crosses these with the schema's (type, permission) declarations.
/// </summary>
public static class RebuildEnumeration
{
    public sealed record RebuildInputs(
        IReadOnlyList<string> Users,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ObjectIdsByType);

    public static async Task<RebuildInputs> LoadAsync(NpgsqlConnection conn, TenantContext t, CancellationToken ct = default)
    {
        // Concrete user subjects (the candidate query subjects); the public "*" is added by the rebuilder.
        var users = (await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT subject_id FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant AND subject_type = 'user' AND subject_id <> '*'
            """, new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct))).ToList();

        // All objects grouped by type (the type universe per type).
        var objects = await conn.QueryAsync<(string ObjectType, string ObjectId)>(new CommandDefinition("""
            SELECT DISTINCT object_type AS ObjectType, object_id AS ObjectId FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
            """, new { store = t.Store, tenant = t.Tenant }, cancellationToken: ct));

        var byType = objects
            .GroupBy(o => o.ObjectType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(o => o.ObjectId).ToList(), StringComparer.Ordinal);

        return new RebuildInputs(users, byType);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter RebuildEnumerationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Index/RebuildEnumeration.cs tests/Relkit.Storage.Postgres.Tests/Index/RebuildEnumerationTests.cs
git commit -m "feat: add rebuild domain enumeration over tenant tuples"
```

---

### Task 3: `ReverseIndexRebuilder` — probe, materialize, stamp, mark built

**Files:**
- Create: `src/Relkit.Storage.Postgres/Index/ReverseIndexRebuilder.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexRebuildTests.cs`

**Interfaces:**
- Produces: `ReverseIndexRebuilder(string connectionString, ISchemaStore schemas, IRelationStore relations, IAttributeStore attributes, IIndexStore index)` with
  `Task RebuildAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)`:
  1. loads the active `Schema` (its `Version` is the stamp); throws `UnknownTypeException` if none active,
  2. `index.ClearAsync(t, uow)` — drop all prior rows for the tenant (and markers),
  3. loads `RebuildInputs`,
  4. constructs an `EngineDrivenAuthorizer` over the **same stores** with `NullConditionEvaluator`,
  5. for each schema `(type, permission)`, each candidate object id of that type, and each candidate subject (`user:{id}` ∪ `user:*`), probes `CheckStructuralAsync`; for each granted probe upserts a `ReverseIndexRow(subject, permission, type, objectId, conditioned)` via `index.UpsertAsync(t, schema.Version, …, uow)`,
  6. `index.MarkBuiltAsync(t, schema.Version, uow)`.
  All writes go on the caller's `uow` so the rebuild commits atomically.
- Consumes: `RebuildEnumeration` (Task 2); `EngineDrivenAuthorizer.CheckStructuralAsync` (Task 1); `IIndexStore` (`m2/01`); `SchemaIndex`, `NullConditionEvaluator` (`m0/05`); the Npgsql stores (`m1/04`).

> **CALIBRATION.** This is the maintenance oracle. It is deliberately the simple, exhaustive, obviously-correct construction: probe every candidate grant with the proven pointwise Check, store the granted ones. The **tests are the spec** — the stored rows must equal what the `m0/07` oracle's `ListObjects` returns structurally for each subject. The `conditioned` flag is set from the probe's `ConditionTouched` latch. The incremental path (`m2/03`) must produce identical rows; the `m2/06` harness proves it.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexRebuildTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ReverseIndexRebuildTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlIndexStore _index = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Condition("within_hours", c => c.Int("start").Int("end"))
        .Build();

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _index = new NpgsqlIndexStore(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<TenantContext> SeedAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, Build(), u);
        if (tuples.Length > 0) await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return t;
    }

    private ReverseIndexRebuilder NewRebuilder() =>
        new(fx.ConnectionString, _schemas, _relations, _attributes, _index);

    private async Task RebuildAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        await NewRebuilder().RebuildAsync(t, u);
        await u.CommitAsync();
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Rebuild_stores_structural_grants_and_marks_built()
    {
        var t = await SeedAsync("rb-basic",
            Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tup("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            Tup("species", "wallaby", "blocked", new SubjectRef("user", "alice")),   // alice revoked on wallaby
            Tup("group", "macropods", "member", new SubjectRef("user", "alice")));
        await RebuildAsync(t);

        (await _index.IsBuiltAsync(t, "v1")).ShouldBeTrue();
        var alice = await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "species", 100, null);
        alice.Select(r => r.ObjectId).ShouldBe(["kangaroo"]);   // wallaby excluded by blocked => no row
        alice.ShouldAllBe(r => r.Conditioned == false);
    }

    [Fact]
    public async Task Rebuild_stores_a_wildcard_grant_under_the_star_subject()
    {
        var t = await SeedAsync("rb-wild",
            Tup("species", "emu", "editor", new SubjectRef("user", "*")));
        await RebuildAsync(t);

        var star = await _index.QueryObjectsAsync(t, "v1", "user:*", "edit", "species", 100, null);
        star.Select(r => r.ObjectId).ShouldBe(["emu"]);
    }

    [Fact]
    public async Task Rebuild_flags_conditioned_grants()
    {
        var t = await SeedAsync("rb-cond",
            Tup("species", "kangaroo", "editor", new SubjectRef("user", "dr-smith")) with
            {
                Condition = new ConditionRef("within_hours",
                    new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 })
            });
        await RebuildAsync(t);

        var rows = await _index.QueryObjectsAsync(t, "v1", "user:dr-smith", "edit", "species", 100, null);
        rows.ShouldHaveSingleItem();
        rows[0].ObjectId.ShouldBe("kangaroo");
        rows[0].Conditioned.ShouldBeTrue();   // stored but flagged for query-time re-check
    }

    [Fact]
    public async Task Rebuild_is_idempotent_and_replaces_prior_rows()
    {
        var t = await SeedAsync("rb-idem",
            Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")));
        await RebuildAsync(t);
        await RebuildAsync(t);   // second rebuild must not duplicate

        var rows = await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "species", 100, null);
        rows.ShouldHaveSingleItem().ObjectId.ShouldBe("kangaroo");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReverseIndexRebuildTests`
Expected: FAIL — `ReverseIndexRebuilder` does not exist.

- [ ] **Step 3: Implement the rebuilder**

```csharp
// src/Relkit.Storage.Postgres/Index/ReverseIndexRebuilder.cs
using Npgsql;
using Relkit.Abstractions;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;

namespace Relkit.Storage.Postgres.Index;

/// <summary>
/// Full rebuild of reverse_index for a (store, tenant): the always-correct safety net and the
/// maintenance oracle (spec §7.3). Materializes the m0/07 ListObjects oracle's structural truth as
/// stored rows by probing every candidate (subject, permission, object) with the unconditioned
/// pointwise Check (NullConditionEvaluator) and keeping the granted ones, each stamped with the
/// active schema_version and flagged conditioned when the grant path touched a condition.
/// </summary>
public sealed class ReverseIndexRebuilder(
    string connectionString,
    ISchemaStore schemas,
    IRelationStore relations,
    IAttributeStore attributes,
    IIndexStore index)
{
    public async Task RebuildAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var schema = await schemas.GetActiveAsync(t.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{t.Store}'>");
        var schemaIndex = new SchemaIndex(schema);

        // 1) Drop all prior rows + markers for the tenant (idempotent rebuild).
        await index.ClearAsync(t, uow, ct);

        // 2) Project the rebuild domain (concrete users + objects-by-type).
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var inputs = await RebuildEnumeration.LoadAsync(conn, t, ct);

        // 3) The oracle authorizer, conditions treated as satisfied (structural truth).
        var authorizer = new EngineDrivenAuthorizer(schemas, relations, attributes, new NullConditionEvaluator());

        // Candidate query subjects: every concrete user, plus the public wildcard.
        var subjects = new List<SubjectRef>(inputs.Users.Count + 1);
        foreach (var uid in inputs.Users) subjects.Add(new SubjectRef("user", uid));
        subjects.Add(new SubjectRef("user", "*"));

        var context = new RequestContext(DateTimeOffset.UnixEpoch,
            new SubjectRef("user", "<rebuild>"), new Dictionary<string, object?>());

        var batch = new List<ReverseIndexRow>(capacity: 256);

        // 4) Cross schema (type, permission) × objects-of-type × subjects.
        foreach (var typeDef in schema.Types)
        {
            if (typeDef.Permissions.Count == 0) continue;
            if (!inputs.ObjectIdsByType.TryGetValue(typeDef.Name, out var objectIds)) continue;

            foreach (var perm in typeDef.Permissions)
            foreach (var oid in objectIds)
            {
                var obj = new EntityRef(typeDef.Name, oid);
                foreach (var subject in subjects)
                {
                    var grant = await authorizer.CheckStructuralAsync(
                        schemaIndex, t, obj, perm.Name, subject, context, ct);
                    if (!grant.Granted) continue;
                    batch.Add(new ReverseIndexRow(subject.ToString(), perm.Name, typeDef.Name, oid, grant.Conditioned));
                }
            }
        }

        // 5) Persist + mark built (on the caller's unit of work).
        await index.UpsertAsync(t, schema.Version, batch, uow, ct);
        await index.MarkBuiltAsync(t, schema.Version, uow, ct);
    }
}
```

> **Why `CheckStructuralAsync` and not `ListObjects`.** Reusing the oracle's per-`(subject, object, permission)` probe keeps the `conditioned` flag exact (the latch is per-check) and keeps the rebuild trivially correct. Materializing via `ListObjects` per subject would be equivalent for granted ids but would not surface the per-row `conditioned` bit, so the probe is the right primitive. At the spec's scale the probe count is bounded and the rebuild is the rare safety-net operation, not the per-request path.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReverseIndexRebuildTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Index/ReverseIndexRebuilder.cs tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexRebuildTests.cs
git commit -m "feat: implement full reverse-index rebuild as the maintenance oracle"
```

---

### Task 4: Rebuild equals the oracle's ListObjects (the safety-net invariant)

**Files:**
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/RebuildEqualsOracleTests.cs`

**Interfaces:**
- Produces: a test pinning the core invariant — for the §12.5-style structural gate and a multi-path object, the rebuilt index's unconditioned rows, read per subject, equal the `EngineDrivenAuthorizer.ListObjects` answer. This is the concrete-case anchor the `m2/06` property harness generalizes.
- Consumes: `ReverseIndexRebuilder`, `NpgsqlIndexStore`, `EngineDrivenAuthorizer`, the Npgsql stores.

> No new production code — this task is a correctness anchor. It proves the rebuild materializes the oracle, so `m2/03` has a trustworthy maintenance oracle to diff against.

- [ ] **Step 1: Write the test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/RebuildEqualsOracleTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class RebuildEqualsOracleTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlIndexStore _index = null!;

    // Multi-path: kangaroo is editable by alice via BOTH a direct grant and group membership.
    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Build();

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _index = new NpgsqlIndexStore(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rebuilt_index_rows_equal_the_oracle_list_objects_per_subject()
    {
        var t = new TenantContext("rb-oracle", "t");
        RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
                new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
                new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, Build(), u);
            await _relations.WriteAsync(t,
            [
                Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")),               // direct path
                Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")), // group path (same object)
                Tup("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
                Tup("species", "wallaby", "blocked", new SubjectRef("user", "alice")),                // alice revoked on wallaby
                Tup("group", "macropods", "member", new SubjectRef("user", "alice")),
                Tup("group", "macropods", "member", new SubjectRef("user", "bob")),
            ], [], u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        var oracle = new EngineDrivenAuthorizer(_schemas, _relations, _attributes, new NullConditionEvaluator());
        foreach (var user in new[] { "alice", "bob" })
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>());
            var oracleIds = (await oracle.ListObjectsAsync(new ListObjectsRequest(
                t, new SubjectRef("user", user), "species", "edit", ctx, PageSize: 1000))).ObjectIds.OrderBy(x => x);
            var indexIds = (await _index.QueryObjectsAsync(t, "v1", $"user:{user}", "edit", "species", 1000, null))
                .Select(r => r.ObjectId).OrderBy(x => x);
            indexIds.ShouldBe(oracleIds, $"index must equal oracle for {user}");
        }
    }
}
```

- [ ] **Step 2: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter RebuildEqualsOracleTests`
Expected: PASS. (alice: kangaroo only — multi-path collapses to one row, wallaby blocked; bob: kangaroo + wallaby.)

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Index/RebuildEqualsOracleTests.cs
git commit -m "test: assert rebuilt index equals oracle list objects per subject"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] The rebuild runs through `EngineDrivenAuthorizer` (the oracle) with `NullConditionEvaluator`, so stored rows are structural and `conditioned` is set from the `ConditionTouched` latch.
- [ ] Rebuild stamps every row with the active `schema.Version` and calls `MarkBuiltAsync` for that version (spec §7.3 stamp).
- [ ] A multi-path object yields exactly one row per `(subject, permission, type, objectId)` (the natural key collapses paths) — proven in Task 4.
- [ ] Exclusion is honoured: a `blocked` grant removes the row (no grant => no row), proven in Task 3.
- [ ] Rebuild is idempotent: `ClearAsync` precedes re-materialization; a second rebuild produces the same rows.
- [ ] All index writes go on the caller's `IUnitOfWork`, so the rebuild commits atomically (spec §9.2).
- [ ] The rebuilt index equals `EngineDrivenAuthorizer.ListObjects` per subject (Task 4) — the safety-net invariant `m2/03` is diffed against.

## Contract gaps / additions (reported, not changed)

- **`EngineDrivenAuthorizer.CheckStructuralAsync` + `StructuralGrant` (new public engine surface).** Task 1 adds a public structural-grant probe on the oracle authorizer so the cross-assembly rebuilder (`Relkit.Storage.Postgres`) can call it, matching the existing precedent that `SchemaIndex`/`EvalContext`/`EvaluationOptions` are public in `Relkit.Core.Evaluation`. This is an addition to `Relkit.Core`, not to the `Relkit.Abstractions` canonical contract, so `../README.md` is unchanged; reported here for visibility. Otherwise reuses `IIndexStore`/`ReverseIndexRow` (`m2/01`) and the existing Npgsql stores.
