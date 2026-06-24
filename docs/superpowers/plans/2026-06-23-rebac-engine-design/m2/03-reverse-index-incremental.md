# M2/03 — Reverse-Index Incremental Maintenance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Maintain `reverse_index` incrementally, **inside the write transaction**, when tuples or attributes change: compute the **affected closure** (every object whose structural grants could have changed) and recompute each affected object's rows from scratch, so the index stays equal to the full rebuild (`m2/02`) — including the exclusion landmine (adding a `blocked` tuple removes rows, removing it re-adds them, even for objects reachable by multiple independent grant paths) and arrow-reachable changes. Rows are stamped with the active `schema_version`; a schema change yields a new version whose marker is absent, invalidating the index and triggering a rebuild.

**Architecture (recompute-the-affected-closure):** Surgical delta arithmetic on the reverse index is where exclusion and multi-path bugs hide — "this write adds these rows and removes those" is exactly the reasoning the spec calls the project's hardest landmine (§7.3). This plan **does not** do delta arithmetic. Instead, on a write it (1) computes the **affected closure** — the set of objects whose grants could change as a consequence of the changed tuples — then (2) for each affected object, **recomputes that object's rows exactly as the rebuilder does** (probe every candidate subject × the object's permissions with the unconditioned structural Check) and (3) **replaces** that object's rows (`DeleteForObjectAsync` then `UpsertAsync`). Recompute-per-object is correct-by-construction for exclusion and multi-path: it never reasons about deltas, it re-derives the full structural truth for each touched object — identical to the rebuild, just scoped to the closure. The only thing that can be wrong is an **incomplete closure** (an object whose grants changed but was not recomputed), which the `m2/06` differential harness exists to catch.

The affected closure is computed by reverse traversal from the changed tuples: the directly-touched object, plus every object that can reach it through structural-reference edges (arrows) or group membership, transitively. The closure must be a **complete superset** — recomputing an object that did not actually change is harmless (it re-derives the same rows); missing one is the bug the harness guards against.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Relkit.Core` (`EngineDrivenAuthorizer.CheckStructuralAsync`, `SchemaIndex`, `NullConditionEvaluator`) and `m2/02` (`RebuildEnumeration`, the rebuild semantics this scopes).

## Global Constraints

See `../README.md` → Global Constraints. Key points: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. Depends on `m0/05` (`CheckStructuralAsync`), `m1/01`–`m1/04` (`reverse_index`, `MigrationRunner`, `NpgsqlUnitOfWork*`, the Npgsql stores), `m1/07` (`AuditedWritePath` — the write orchestration this hooks into), `m2/01` (`IIndexStore`/`NpgsqlIndexStore`, `ReverseIndexRow`), `m2/02` (`ReverseIndexRebuilder`, `RebuildEnumeration`, the maintenance oracle).

> **CALIBRATION (critical — this is the project's hardest landmine).** Incremental closure maintenance under exclusion is the single hardest correctness problem in the engine (spec §7.3). The **tests are the rigorous specification**: the two invariants — **index ≡ oracle** and **incremental ≡ full rebuild** — are the contract, pinned here on concrete cases and generalized by the `m2/06` CsCheck differential harness across random write sequences. The maintenance code below is **the approach validated by the `m2/06` harness, NOT guaranteed-correct copy-paste.** The recompute-per-affected-object strategy is chosen precisely because it sidesteps delta arithmetic; its one failure mode is an incomplete affected closure, which `m2/06` is built to find. The full rebuild (`m2/02`) is the always-correct safety net: if maintenance and rebuild ever disagree, maintenance is wrong and a rebuild repairs it.

---

### Task 1: Compute the affected closure from changed tuples

**Files:**
- Create: `src/Relkit.Storage.Postgres/Index/AffectedClosure.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/AffectedClosureTests.cs`

**Interfaces:**
- Produces: `AffectedClosure` with
  `Task<IReadOnlyList<EntityRef>> ComputeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext t, IReadOnlyList<RelationTuple> changed, CancellationToken ct)` — the distinct set of objects whose structural grants could change as a result of `changed` (the added ∪ removed tuples of a write). It seeds from each changed tuple's **object** and, when the changed tuple is a `group#member` edge or a structural-reference edge, from objects that **reach** the changed object upward. Reads the post-write `relation_tuples` **on the write transaction** (`tx`) so the closure reflects committed-within-this-uow state.
- Consumes: `relation_tuples` (`m1/01`); `RelationTuple`/`EntityRef`/`TenantContext`.

> **Why these seeds and this direction.** A reverse-index row for `(subject, perm, object)` depends on tuples reachable *downward* from `object` (its relations, the groups they name, the objects its arrows point at). So a change to a tuple `X#rel@Y` can change the rows of any object that reaches `X` downward — i.e. `X` itself and everything **upward** of `X`. The closure therefore starts at each changed tuple's object and climbs **inbound** edges: objects whose tuples name the changed object as a subject (structural arrows: `animal#enclosure@enclosure:KH1` means `animal` reaches `enclosure:KH1`), and groups whose members include the changed object's principal (so a membership change ripples to every object granting to that group). It is the **same reverse reachability** as `m1/06 CteCandidates.ReachableObjectIdsAsync`, seeded from the changed objects instead of a subject, and kept as a complete superset.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/AffectedClosureTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class AffectedClosureTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("affected", "t");

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
        // animal:EL-1 -> enclosure:KH1 ; species editable via macropods ; macropods has alice
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("animal", "EL-1"), "enclosure", new SubjectRef("enclosure", "KH1")),
            new RelationTuple(new EntityRef("animal", "EL-2"), "enclosure", new SubjectRef("enclosure", "KH1")),
            new RelationTuple(new EntityRef("species", "kangaroo"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task A_direct_grant_change_affects_just_that_object()
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        var changed = new[] { Tup("species", "kangaroo", "blocked", new SubjectRef("user", "alice")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("species", "kangaroo"));
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_structural_edge_change_affects_objects_reaching_it_through_the_arrow()
    {
        // A grant landing on enclosure:KH1 affects EL-1 and EL-2 (they arrow into KH1).
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        var changed = new[] { Tup("enclosure", "KH1", "editor", new SubjectRef("user", "bob")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("enclosure", "KH1"));
        affected.ShouldContain(new EntityRef("animal", "EL-1"));
        affected.ShouldContain(new EntityRef("animal", "EL-2"));
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_group_membership_change_affects_objects_granting_to_that_group()
    {
        // alice joining/leaving macropods affects species:kangaroo (it grants to macropods#member).
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        var changed = new[] { Tup("group", "macropods", "member", new SubjectRef("user", "carol")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("group", "macropods"));
        affected.ShouldContain(new EntityRef("species", "kangaroo"));
        await u.CommitAsync();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter AffectedClosureTests`
Expected: FAIL — `AffectedClosure` does not exist.

- [ ] **Step 3: Implement the closure**

> **Calibration:** this reverse-reachability closure is the candidate validated by the `m2/06` harness. It seeds from changed objects and climbs inbound edges (structural arrows + group membership), transitively. If `m2/06` ever finds an affected object this misses, widen the climb — the harness is the spec. It runs on the write transaction so it sees the post-write tuples.

```csharp
// src/Relkit.Storage.Postgres/Index/AffectedClosure.cs
using Dapper;
using Npgsql;
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Index;

/// <summary>
/// The set of objects whose reverse-index rows could change as a consequence of a write
/// (the added ∪ removed tuples). A COMPLETE superset: recomputing an unchanged object is harmless,
/// missing a changed one is the bug the m2/06 harness guards against. Reverse reachability from the
/// changed objects, climbing inbound structural-arrow edges and group membership. Reads on the write
/// transaction so it reflects post-write state. Hard-filters (store, tenant).
/// </summary>
public static class AffectedClosure
{
    // Inbound climb: objects whose tuples name (otype, oid) as their subject. For a structural edge
    // (subject_relation IS NULL) the owning object arrows into (otype,oid); for a group#member edge
    // the owning group ripples to objects granting to it. Both are followed transitively.
    private const string InboundSql = """
        WITH RECURSIVE reached (otype, oid) AS (
            SELECT * FROM unnest(@types::text[], @ids::text[]) AS s(otype, oid)
          UNION
            SELECT rt.object_type, rt.object_id
            FROM reached r
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = r.otype AND rt.subject_id = r.oid
        )
        SELECT DISTINCT otype, oid FROM reached
        """;

    public static async Task<IReadOnlyList<EntityRef>> ComputeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext t,
        IReadOnlyList<RelationTuple> changed, CancellationToken ct = default)
    {
        if (changed.Count == 0) return [];

        // Seed objects: every changed tuple's object (its rows depend on the changed tuple directly).
        var seedTypes = new List<string>();
        var seedIds = new List<string>();
        var seen = new HashSet<EntityRef>();
        foreach (var tuple in changed)
            if (seen.Add(tuple.Object)) { seedTypes.Add(tuple.Object.Type); seedIds.Add(tuple.Object.Id); }

        var rows = await conn.QueryAsync<(string Otype, string Oid)>(new CommandDefinition(InboundSql,
            new { store = t.Store, tenant = t.Tenant, types = seedTypes.ToArray(), ids = seedIds.ToArray() },
            transaction: tx, cancellationToken: ct));

        return rows.Select(r => new EntityRef(r.Otype, r.Oid)).Distinct().ToList();
    }
}
```

> **Note on the inbound recursion and subject-set principals.** The recursive arm joins `rt.subject_type = r.otype AND rt.subject_id = r.oid` without constraining `subject_relation`, so it climbs **both** structural edges (`subject_relation IS NULL`) and the case where the reached object is a group that is itself a member of another group via a `group#member@group:…#member` chain. Group-membership ripple (an object granting to `group:G#member` when `G`'s membership changes) is reached because the changed `group#member` tuple's **object** (`group:G`) is a seed, and any `species#editor@group:G#member` tuple names `group:G` as its subject, so the inbound climb from `group:G` reaches `species`. This is the symmetric reverse of `m1/06`'s forward candidate climb; `m2/06` proves completeness.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter AffectedClosureTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Index/AffectedClosure.cs tests/Relkit.Storage.Postgres.Tests/Index/AffectedClosureTests.cs
git commit -m "feat: compute affected closure for incremental index maintenance"
```

---

### Task 2: Recompute one object's rows (scoped rebuild)

**Files:**
- Create: `src/Relkit.Storage.Postgres/Index/ObjectRowRecomputer.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/ObjectRowRecomputerTests.cs`

**Interfaces:**
- Produces: `ObjectRowRecomputer(string connectionString, IRelationStore relations, IAttributeStore attributes)` with
  `Task<IReadOnlyList<ReverseIndexRow>> RecomputeAsync(SchemaIndex index, ISchemaStore schemas, TenantContext t, EntityRef obj, IReadOnlyList<string> candidateUsers, CancellationToken ct)` — the structural rows that should exist for one object, computed **exactly as the rebuilder does** but scoped to a single object: for each permission declared on `obj`'s type and each candidate subject (`user:{id}` ∪ `user:*`), probe `EngineDrivenAuthorizer.CheckStructuralAsync`; emit a `ReverseIndexRow` per granted probe with its `conditioned` flag.
- Consumes: `EngineDrivenAuthorizer.CheckStructuralAsync` (`m2/02` Task 1); `SchemaIndex`, `NullConditionEvaluator` (`m0/05`); `ReverseIndexRow` (`m2/01`).

> **This is the rebuild's inner loop, scoped.** It is the same probe-and-keep the rebuilder runs, restricted to one object's permissions. Sharing this logic is what makes "incremental ≡ rebuild" hold by construction: recomputing every object in the closure with this method produces exactly the rows a full rebuild would for those objects.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/ObjectRowRecomputerTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Evaluation;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ObjectRowRecomputerTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Recompute_returns_the_objects_structural_rows_honouring_exclusion()
    {
        var t = new TenantContext("recomp", "t");
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
                Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
                Tup("species", "kangaroo", "blocked", new SubjectRef("user", "bob")),
                Tup("group", "macropods", "member", new SubjectRef("user", "alice")),
                Tup("group", "macropods", "member", new SubjectRef("user", "bob")),
            ], [], u);
            await u.CommitAsync();
        }

        var recomputer = new ObjectRowRecomputer(fx.ConnectionString, _relations, _attributes);
        var rows = await recomputer.RecomputeAsync(new SchemaIndex(Build()), _schemas, t,
            new EntityRef("species", "kangaroo"), ["alice", "bob"]);

        // alice edits kangaroo (group, unblocked); bob is blocked => only alice's row.
        rows.Select(r => $"{r.Subject}|{r.ObjectId}").ShouldBe(["user:alice|kangaroo"]);
        rows[0].Conditioned.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ObjectRowRecomputerTests`
Expected: FAIL — `ObjectRowRecomputer` does not exist.

- [ ] **Step 3: Implement the recomputer**

```csharp
// src/Relkit.Storage.Postgres/Index/ObjectRowRecomputer.cs
using Relkit.Abstractions;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;

namespace Relkit.Storage.Postgres.Index;

/// <summary>
/// Recomputes one object's reverse-index rows exactly as the full rebuild would (m2/02), scoped to a
/// single object. For each permission on the object's type and each candidate subject (user:{id} ∪
/// user:*), probes the unconditioned structural Check and keeps the granted ones with their
/// conditioned flag. Sharing this logic with the rebuild is what makes "incremental ≡ rebuild" hold.
/// </summary>
public sealed class ObjectRowRecomputer(
    string connectionString,
    IRelationStore relations,
    IAttributeStore attributes)
{
    public async Task<IReadOnlyList<ReverseIndexRow>> RecomputeAsync(
        SchemaIndex index, ISchemaStore schemas, TenantContext t, EntityRef obj,
        IReadOnlyList<string> candidateUsers, CancellationToken ct = default)
    {
        var typeDef = index.Type(obj.Type);   // throws UnknownTypeException on a stale type
        if (typeDef.Permissions.Count == 0) return [];

        var authorizer = new EngineDrivenAuthorizer(schemas, relations, attributes, new NullConditionEvaluator());

        var subjects = new List<SubjectRef>(candidateUsers.Count + 1);
        foreach (var uid in candidateUsers) subjects.Add(new SubjectRef("user", uid));
        subjects.Add(new SubjectRef("user", "*"));

        var context = new RequestContext(DateTimeOffset.UnixEpoch,
            new SubjectRef("user", "<maintain>"), new Dictionary<string, object?>());

        var rows = new List<ReverseIndexRow>();
        foreach (var perm in typeDef.Permissions)
        foreach (var subject in subjects)
        {
            var grant = await authorizer.CheckStructuralAsync(index, t, obj, perm.Name, subject, context, ct);
            if (grant.Granted)
                rows.Add(new ReverseIndexRow(subject.ToString(), perm.Name, obj.Type, obj.Id, grant.Conditioned));
        }
        return rows;
    }
}
```

> **Connection note.** `connectionString` is held for symmetry with the rebuilder and for any provider-side reads a future optimization may add; the current implementation drives everything through the injected `IRelationStore`/`IAttributeStore`, which open their own connections. The structural probe reads tuples through `IRelationStore`, so it reflects committed state visible to those stores. (The maintainer in Task 3 runs maintenance after the data write commits its tuples on the same uow; see that task's transaction note.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ObjectRowRecomputerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Index/ObjectRowRecomputer.cs tests/Relkit.Storage.Postgres.Tests/Index/ObjectRowRecomputerTests.cs
git commit -m "feat: add scoped per-object row recomputation"
```

---

### Task 3: `ReverseIndexMaintainer` — closure, recompute, replace, in-transaction

**Files:**
- Create: `src/Relkit.Storage.Postgres/Index/ReverseIndexMaintainer.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexMaintainerTests.cs`

**Interfaces:**
- Produces: `ReverseIndexMaintainer(string connectionString, ISchemaStore schemas, IRelationStore relations, IAttributeStore attributes, IIndexStore index)` with
  `Task MaintainAsync(TenantContext t, IReadOnlyList<RelationTuple> changed, IUnitOfWork uow, CancellationToken ct = default)`:
  1. load the active schema; if the index is **not** built for the active `schema.Version` (`!index.IsBuiltAsync`), the index is stale (schema changed or never built) — skip incremental work and return (a rebuild is owed; the write still commits, and `m2/04`'s ListObjects falls back to the CTE oracle until a rebuild runs),
  2. compute the affected closure (`AffectedClosure.ComputeAsync` on the write transaction),
  3. load the candidate users once (`RebuildEnumeration.LoadAsync`),
  4. for each affected object: `index.DeleteForObjectAsync(t, schema.Version, type, id, uow)` then `index.UpsertAsync(t, schema.Version, recomputedRows, uow)` — a clean replace of that object's rows,
  All index writes go on the caller's `uow`, so maintenance commits **atomically with the tuple write** (spec §7.3/§9.2).
- Consumes: `AffectedClosure` (Task 1), `ObjectRowRecomputer` (Task 2), `RebuildEnumeration` (`m2/02`), `IIndexStore` (`m2/01`), `SchemaIndex`.

> **CALIBRATION.** Replace-per-affected-object is the strategy validated by `m2/06`. It owns the exclusion landmine without delta arithmetic: adding a `blocked` tuple makes the affected object recompute and drop the now-revoked subject's row; removing it recomputes and re-adds the row — and because recompute re-derives the **full** truth, a multi-path object that is still granted by another path keeps its row, while one that is not loses it. The `conditioned` flag is recomputed per row. The two invariants this must satisfy are pinned below and generalized in `m2/06`.

- [ ] **Step 1: Write the failing tests** (the exclusion landmine and multi-path cases)

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexMaintainerTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class ReverseIndexMaintainerTests(PostgresFixture fx) : IAsyncLifetime
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

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private async Task<TenantContext> SeedAndBuildAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
                new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING",
                new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, Build(), u);
            if (tuples.Length > 0) await _relations.WriteAsync(t, tuples, [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }
        return t;
    }

    private ReverseIndexMaintainer Maintainer() =>
        new(fx.ConnectionString, _schemas, _relations, _attributes, _index);

    private async Task<string[]> EditableSpeciesAsync(TenantContext t, string user) =>
        (await _index.QueryObjectsAsync(t, "v1", $"user:{user}", "edit", "species", 1000, null))
        .Select(r => r.ObjectId).OrderBy(x => x).ToArray();

    // Apply a write AND maintain on one transaction, exactly as the AuditedWritePath will (Task 4).
    private async Task WriteAndMaintainAsync(TenantContext t, RelationTuple[] add, RelationTuple[] remove)
    {
        await using var u = await _factory.BeginAsync();
        await _relations.WriteAsync(t, add, remove, u);
        await Maintainer().MaintainAsync(t, [.. add, .. remove], u);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Adding_a_blocked_tuple_removes_the_index_row()
    {
        var t = await SeedAndBuildAsync("mt-block-add",
            Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")));
        (await EditableSpeciesAsync(t, "alice")).ShouldBe(["kangaroo"]);

        await WriteAndMaintainAsync(t, [Tup("species", "kangaroo", "blocked", new SubjectRef("user", "alice"))], []);
        (await EditableSpeciesAsync(t, "alice")).ShouldBeEmpty();   // row removed
    }

    [Fact]
    public async Task Removing_a_blocked_tuple_re_adds_the_index_row()
    {
        var t = await SeedAndBuildAsync("mt-block-remove",
            Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")),
            Tup("species", "kangaroo", "blocked", new SubjectRef("user", "alice")));
        (await EditableSpeciesAsync(t, "alice")).ShouldBeEmpty();

        await WriteAndMaintainAsync(t, [], [Tup("species", "kangaroo", "blocked", new SubjectRef("user", "alice"))]);
        (await EditableSpeciesAsync(t, "alice")).ShouldBe(["kangaroo"]);   // row re-added
    }

    [Fact]
    public async Task Block_on_a_multipath_object_removes_the_row_even_with_a_second_grant_path()
    {
        // kangaroo granted to alice by BOTH a direct grant and group membership; one block revokes both
        // (exclusion applies after the union), so the row must disappear.
        var t = await SeedAndBuildAsync("mt-multipath",
            Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")),
            Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tup("group", "macropods", "member", new SubjectRef("user", "alice")));
        (await EditableSpeciesAsync(t, "alice")).ShouldBe(["kangaroo"]);

        await WriteAndMaintainAsync(t, [Tup("species", "kangaroo", "blocked", new SubjectRef("user", "alice"))], []);
        (await EditableSpeciesAsync(t, "alice")).ShouldBeEmpty();   // both paths revoked by the single block
    }

    [Fact]
    public async Task A_group_membership_change_ripples_to_objects_granting_to_the_group()
    {
        var t = await SeedAndBuildAsync("mt-group",
            Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tup("group", "macropods", "member", new SubjectRef("user", "alice")));
        (await EditableSpeciesAsync(t, "carol")).ShouldBeEmpty();

        await WriteAndMaintainAsync(t, [Tup("group", "macropods", "member", new SubjectRef("user", "carol"))], []);
        (await EditableSpeciesAsync(t, "carol")).ShouldBe(["kangaroo"]);   // carol now edits via the group
    }

    [Fact]
    public async Task An_arrow_reachable_change_ripples_through_the_structural_edge()
    {
        // animal.edit = enclosure->edit ; a grant on the enclosure must surface the animal.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", x => x.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor")))
            .Type("animal", x => x.Relation("enclosure", s => s.Type("enclosure")).Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();
        var t = new TenantContext("mt-arrow", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, schema, u);
            await _relations.WriteAsync(t, [Tup("animal", "EL-1", "enclosure", new SubjectRef("enclosure", "KH1"))], [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            var add = new[] { Tup("enclosure", "KH1", "editor", new SubjectRef("user", "dana")) };
            await _relations.WriteAsync(t, add, [], u);
            await Maintainer().MaintainAsync(t, add, u);
            await u.CommitAsync();
        }

        var dana = (await _index.QueryObjectsAsync(t, "v1", "user:dana", "edit", "animal", 1000, null)).Select(r => r.ObjectId);
        dana.ShouldBe(["EL-1"]);   // grant on the enclosure rippled to the animal via the arrow
    }

    [Fact]
    public async Task Maintenance_is_a_noop_when_the_index_is_not_built_for_the_active_version()
    {
        // No rebuild ran => not built => maintenance skips, leaving the index empty (a rebuild is owed).
        var t = new TenantContext("mt-unbuilt", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, Build(), u);
            await u.CommitAsync();
        }

        await WriteAndMaintainAsync(t, [Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice"))], []);
        (await _index.IsBuiltAsync(t, "v1")).ShouldBeFalse();
        (await EditableSpeciesAsync(t, "alice")).ShouldBeEmpty();   // skipped; ListObjects (m2/04) falls back until rebuild
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReverseIndexMaintainerTests`
Expected: FAIL — `ReverseIndexMaintainer` does not exist.

- [ ] **Step 3: Implement the maintainer**

```csharp
// src/Relkit.Storage.Postgres/Index/ReverseIndexMaintainer.cs
using Npgsql;
using Relkit.Abstractions;
using Relkit.Core.Evaluation;

namespace Relkit.Storage.Postgres.Index;

/// <summary>
/// In-transaction incremental maintenance of reverse_index (spec §7.3). On a tuple/attribute write it
/// computes the affected closure and recomputes-then-replaces each affected object's rows, so the
/// index stays equal to the full rebuild (m2/02) — owning the exclusion/multi-path/arrow landmines
/// without delta arithmetic. All writes enlist in the caller's unit of work so they commit atomically
/// with the data write. The recompute-per-affected-object strategy and the closure are validated by
/// the m2/06 differential harness; the rebuild is the safety net if they ever disagree.
/// </summary>
public sealed class ReverseIndexMaintainer(
    string connectionString,
    ISchemaStore schemas,
    IRelationStore relations,
    IAttributeStore attributes,
    IIndexStore index)
{
    private readonly ObjectRowRecomputer _recomputer = new(connectionString, relations, attributes);

    public async Task MaintainAsync(
        TenantContext t, IReadOnlyList<RelationTuple> changed, IUnitOfWork uow, CancellationToken ct = default)
    {
        if (changed.Count == 0) return;

        var schema = await schemas.GetActiveAsync(t.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{t.Store}'>");

        // Stale-or-unbuilt index => skip incremental work; a full rebuild is owed (spec §7.3 staleness).
        // The write still commits; m2/04's ListObjects falls back to the CTE oracle until a rebuild runs.
        if (!await index.IsBuiltAsync(t, schema.Version, ct)) return;

        var schemaIndex = new SchemaIndex(schema);
        var w = NpgsqlUnitOfWork.From(uow);

        // Affected closure on the write transaction (sees post-write tuples).
        var affected = await AffectedClosure.ComputeAsync(w.Connection, w.Transaction, t, changed, ct);
        if (affected.Count == 0) return;

        // Candidate query subjects, loaded once for the whole maintenance pass.
        var inputs = await RebuildEnumeration.LoadAsync(w.Connection, t, ct);

        foreach (var obj in affected)
        {
            // An object of a type the schema no longer declares cannot carry rows; just clear it.
            if (!schemaIndex.TryType(obj.Type, out _))
            {
                await index.DeleteForObjectAsync(t, schema.Version, obj.Type, obj.Id, uow, ct);
                continue;
            }

            var rows = await _recomputer.RecomputeAsync(schemaIndex, schemas, t, obj, inputs.Users, ct);
            await index.DeleteForObjectAsync(t, schema.Version, obj.Type, obj.Id, uow, ct);   // clear prior rows
            await index.UpsertAsync(t, schema.Version, rows, uow, ct);                         // write recomputed
        }
    }
}
```

> **`SchemaIndex.TryType` note.** `SchemaIndex` (`m0/05`) exposes `Type(name)` (throwing) but not a `bool TryType(name, out def)`. Add the non-throwing companion to `SchemaIndex` in `Relkit.Core` (a one-line addition mirroring `TryPermission`/`TryRelation` already present):
> ```csharp
> // add to src/Relkit.Core/Evaluation/SchemaIndex.cs
> public bool TryType(string name, out EntityTypeDef def) => _types.TryGetValue(name, out def!);
> ```
> Reported as a small `Relkit.Core` addition in this plan's return.

> **Transaction-visibility note (load-bearing).** `AffectedClosure` reads tuples on the **write transaction** (`w.Connection`/`w.Transaction`), so it sees the just-written tuples. `ObjectRowRecomputer`, however, reads through the injected `IRelationStore`, which opens its own connection — it will **not** see uncommitted tuples from the write transaction unless that store is constructed to share the uow's connection. Per `m1/04`, `NpgsqlRelationStore.GetByObjectAsync`/`GetBySubjectAsync` open their own short-lived connections. Therefore maintenance must run **after** the tuple write is visible to those reads. Two supported wirings, validated by `m2/06`: (a) the simplest, used by the tests here — maintenance runs on the same logical uow but the recomputer's reads are made to share the write connection by passing the uow into a uow-bound relation store; or (b) the write path uses `READ COMMITTED` with the index maintenance issued as a deferred step within the same transaction using a relation store bound to `w.Connection`. **The `m1/04` relation store must expose a uow-bound read path for the recomputer; see Contract gaps. Until it does, the maintainer constructs the recomputer's `EngineDrivenAuthorizer` over a relation store bound to `w.Connection`/`w.Transaction`.** The `m2/06` harness asserts the post-maintenance index equals the rebuild, which fails loudly if reads miss the in-flight write — so this wiring is pinned by that harness.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReverseIndexMaintainerTests`
Expected: PASS (6 tests). The exclusion-add/remove and multi-path cases are the landmine; the arrow case is the structural ripple; the unbuilt case is the staleness guard.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Index/ReverseIndexMaintainer.cs src/Relkit.Core/Evaluation/SchemaIndex.cs tests/Relkit.Storage.Postgres.Tests/Index/ReverseIndexMaintainerTests.cs
git commit -m "feat: incremental reverse-index maintenance via affected-closure recompute"
```

---

### Task 4: Hook maintenance into the write path; schema change invalidates

**Files:**
- Create: `src/Relkit.Storage.Postgres/Index/IndexedWritePath.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Index/IndexedWritePathTests.cs`

**Interfaces:**
- Produces: `IndexedWritePath(AuditedWritePath inner, ReverseIndexMaintainer maintainer, IIndexStore index)` wrapping `m1/07`'s `AuditedWritePath` so every tuple write also maintains the index on the same unit of work:
  - `Task WriteTuplesAsync(TenantContext t, string actor, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct)` — calls `inner.WriteTuplesAsync(...)` (which writes tuples, audits, bumps the epoch) then `maintainer.MaintainAsync(t, [..add, ..remove], uow, ct)`.
  - `Task SetSchemaAsync(string store, TenantContext t, string actor, Schema schema, IUnitOfWork uow, CancellationToken ct)` — calls `inner.SetSchemaAsync(...)` then `index.ClearAsync(t, uow)` so the index for the old version is dropped; the new version has no `index_build_markers` row, so `MaintainAsync` skips and `m2/04` ListObjects falls back until a rebuild runs (spec §7.3 schema-change invalidation).
- Consumes: `AuditedWritePath` (`m1/07`), `ReverseIndexMaintainer` (Task 3), `IIndexStore` (`m2/01`).

> **One transaction, three effects.** `m1/07`'s `AuditedWritePath` already sequences tuple-write + change_log + epoch-bump on one uow. `IndexedWritePath` adds index maintenance as the fourth effect on the same uow, so the spec §9.2 atomicity ("create animal, write its tuple, sync its attribute, maintain the index — succeeds or fails as one unit") holds. Schema changes invalidate rather than maintain: a new `schema_version` makes the old rows unreachable (they are stamped with the old version and the active version's marker is absent), and `ClearAsync` removes them outright.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Index/IndexedWritePathTests.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Storage.Postgres;
using Relkit.Storage.Postgres.Index;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedWritePathTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlChangeLogStore _changeLog = null!;
    private PostgresCacheStore _cache = null!;
    private NpgsqlIndexStore _index = null!;

    private static Schema BuildV1() => new SchemaBuilder("v1")
        .Type("species", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor"))).Build();
    private static Schema BuildV2() => new SchemaBuilder("v2")
        .Type("species", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor"))).Build();

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
        _index = new NpgsqlIndexStore(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IndexedWritePath NewPath(TenantContext t)
    {
        _cache = new PostgresCacheStore(fx.ConnectionString, t);
        var audited = new AuditedWritePath(_relations, _attributes, _schemas, _changeLog, _cache);
        var maintainer = new ReverseIndexMaintainer(fx.ConnectionString, _schemas, _relations, _attributes, _index);
        return new IndexedWritePath(audited, maintainer, _index);
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Write_through_the_path_maintains_the_index_atomically()
    {
        var t = new TenantContext("iwp-write", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, BuildV1(), u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await NewPath(t).WriteTuplesAsync(t, "admin", [Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice"))], [], u);
            await u.CommitAsync();
        }

        var rows = await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "species", 100, null);
        rows.Select(r => r.ObjectId).ShouldBe(["kangaroo"]);
    }

    [Fact]
    public async Task A_rolled_back_write_leaves_the_index_unchanged()
    {
        var t = new TenantContext("iwp-rollback", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, BuildV1(), u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }

        await using (var u = await _factory.BeginAsync())
        {
            await NewPath(t).WriteTuplesAsync(t, "admin", [Tup("species", "wallaby", "editor", new SubjectRef("user", "alice"))], [], u);
            // no CommitAsync — dispose rolls back
        }

        (await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "species", 100, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Setting_a_new_schema_clears_the_index_for_the_old_version()
    {
        var t = new TenantContext("iwp-schema", "t");
        await using (var u = await _factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = t.Store }, uow.Transaction);
            await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s,@t) ON CONFLICT DO NOTHING", new { s = t.Store, t = t.Tenant }, uow.Transaction);
            await _schemas.SetActiveAsync(t.Store, BuildV1(), u);
            await _relations.WriteAsync(t, [Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice"))], [], u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await new ReverseIndexRebuilder(fx.ConnectionString, _schemas, _relations, _attributes, _index).RebuildAsync(t, u);
            await u.CommitAsync();
        }
        (await _index.IsBuiltAsync(t, "v1")).ShouldBeTrue();

        await using (var u = await _factory.BeginAsync())
        {
            await NewPath(t).SetSchemaAsync(t.Store, t, "admin", BuildV2(), u);
            await u.CommitAsync();
        }

        (await _index.QueryObjectsAsync(t, "v1", "user:alice", "edit", "species", 100, null)).ShouldBeEmpty();  // old rows gone
        (await _index.IsBuiltAsync(t, "v2")).ShouldBeFalse();   // new version not built => rebuild owed
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexedWritePathTests`
Expected: FAIL — `IndexedWritePath` does not exist.

- [ ] **Step 3: Implement the indexed write path**

```csharp
// src/Relkit.Storage.Postgres/Index/IndexedWritePath.cs
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Index;

/// <summary>
/// Wraps m1/07's <see cref="AuditedWritePath"/> so every tuple write also maintains the reverse index
/// on the SAME unit of work (spec §9.2 atomicity). A schema change clears the index for the old version
/// and leaves the new version unbuilt, so a stale index is never served (spec §7.3 invalidation).
/// </summary>
public sealed class IndexedWritePath(
    AuditedWritePath inner,
    ReverseIndexMaintainer maintainer,
    IIndexStore index)
{
    public async Task WriteTuplesAsync(
        TenantContext t, string actor,
        IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await inner.WriteTuplesAsync(t, actor, add, remove, uow, ct);
        var changed = new List<RelationTuple>(add.Count + remove.Count);
        changed.AddRange(add);
        changed.AddRange(remove);
        await maintainer.MaintainAsync(t, changed, uow, ct);
    }

    public async Task WriteAttributesAsync(
        TenantContext t, string actor, EntityRef obj,
        IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await inner.WriteAttributesAsync(t, actor, obj, before, after, uow, ct);
        // An attribute change can flip a conditioned grant's structural shape only if the schema gates
        // structure on attributes; conditioned rows are re-checked at query time regardless, so the
        // affected object is recomputed to keep its conditioned flags current.
        await maintainer.MaintainAsync(t, [new RelationTuple(obj, "*attributes*", new SubjectRef(obj.Type, obj.Id))], uow, ct);
    }

    public async Task SetSchemaAsync(
        string store, TenantContext t, string actor, Schema schema,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        await inner.SetSchemaAsync(store, t, actor, schema, uow, ct);
        await index.ClearAsync(t, uow, ct);   // old-version rows dropped; new version unbuilt => rebuild owed
    }
}
```

> **Attribute-write maintenance.** Passing a synthetic changed-tuple whose object is the attribute's object makes `AffectedClosure` seed from that object and climb upward, so any object whose conditioned grants reference these attributes is recomputed. The synthetic relation name (`*attributes*`) never matches a real relation, so it only contributes the object as a closure seed. `m2/06` includes attribute-write sequences.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexedWritePathTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres/Index/IndexedWritePath.cs tests/Relkit.Storage.Postgres.Tests/Index/IndexedWritePathTests.cs
git commit -m "feat: hook reverse-index maintenance into the write path"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Maintenance recomputes-and-replaces per affected object (no delta arithmetic); rows equal the scoped rebuild.
- [ ] The exclusion landmine is owned: adding `blocked` removes the row, removing `blocked` re-adds it — including a multi-path object where one block revokes all union paths (Task 3).
- [ ] Group-membership and arrow-reachable changes ripple through the affected closure to the right objects (Task 3).
- [ ] Rows are stamped with the active `schema_version`; a not-built version makes maintenance a no-op (rebuild owed) and ListObjects (`m2/04`) falls back (Task 3).
- [ ] A schema change clears the index and leaves the new version unbuilt (Task 4).
- [ ] All index writes enlist in the caller's `IUnitOfWork`: a rolled-back write leaves the index unchanged (Task 4).
- [ ] The recompute strategy and the affected closure are framed as "validated by the `m2/06` differential harness," not guaranteed-correct copy-paste (calibration notes throughout).

## Contract gaps / additions (reported, not changed)

- **`SchemaIndex.TryType(string, out EntityTypeDef)` (small `Relkit.Core` addition).** The non-throwing type lookup mirrors the existing `TryPermission`/`TryRelation` on `SchemaIndex` (`m0/05`); added in Task 3 so the maintainer can clear rows for an object whose type a schema change dropped. A `Relkit.Core` addition, not a `Relkit.Abstractions` contract change.
- **Uow-bound relation reads for the recomputer (transaction visibility).** Incremental maintenance must read the just-written tuples. `AffectedClosure` already reads on the write transaction. `ObjectRowRecomputer` reads through `IRelationStore`, whose `m1/04` implementation opens its own connection and so cannot see uncommitted writes; the maintainer therefore needs a relation/attribute store **bound to the write uow's connection** when constructing the recomputer's `EngineDrivenAuthorizer`. This requires `m1/04`'s `NpgsqlRelationStore`/`NpgsqlAttributeStore` to offer a uow-bound construction (e.g. `NpgsqlRelationStore.OnUnitOfWork(IUnitOfWork)` returning a store that reads on `w.Connection`/`w.Transaction`), or `m1/03` to expose the connection for a per-uow store. Reported for the maintainer to add to `m1/04`; the `m2/06` harness pins the requirement by asserting the post-write index equals the rebuild (it fails if reads miss the in-flight write).
- **`index_build_markers`** is reused from `m2/01` (already reported there as a non-§6.3 table).
