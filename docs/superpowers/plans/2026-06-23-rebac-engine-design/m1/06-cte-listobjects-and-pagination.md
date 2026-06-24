# M1/06 — CTE ListObjects, ListSubjects & Pagination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `NpgsqlCteAuthorizer.ListObjectsAsync` and `ListSubjectsAsync` (spec §7.3 Milestone 1 / §7.5) over the Postgres recursive-CTE path, with the **over-fetch/refill** pagination contract and the **same `ContinuationCursor`** shape `m0/07` uses (reused verbatim from `Relkit.Core.Evaluation`). Candidate generation (reverse reachability, type universe for wildcard grants) runs in SQL via recursive CTEs; each candidate is **confirmed by the pointwise CTE Check** from `m1/05`, so exclusion/intersection/conditions are honoured — identical results to the `m0/07` `EngineDrivenAuthorizer` oracle.

**Architecture (the seam decided in `m1/02`, reproduced):** The recursive CTE computes **reachability only** — here, the reverse direction: the distinct objects of the target type reachable from the subject (subject's tuples, the tuples of every group it transitively belongs to, structural-reference edges). That candidate set is a **superset**; correctness comes from re-confirming each candidate with the full pointwise `NpgsqlCteAuthorizer.CheckPermissionAsync` from `m1/05` (which composes the algebra in C#). `ListObjects` is the oracle's Milestone-1 strategy (spec §7.3) ported to Postgres: SQL gathers candidates fast, C# confirms them correctly. `ListSubjects` forward-collects candidate leaf users (relations, nested groups, arrow targets) then confirms each with Check. Pagination is **over-fetch and refill** (spec §7.5): scan candidates in a deterministic ordinal-id order, confirm, return exactly `PageSize` (or fewer only at the true end) with an opaque `ContinuationCursor` encoding the last-confirmed id.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Relkit.Core` (`ContinuationCursor`, `EvalContext`, `SchemaIndex`).

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. Depends on `m0/01` (Abstractions), `m0/07` (`ContinuationCursor` shape, the oracle's ListObjects/ListSubjects semantics this must match), `m1/01` (`relation_tuples`, `MigrationRunner`, `PostgresFixture`), `m1/03` (`NpgsqlUnitOfWorkFactory`), `m1/04` (`NpgsqlRelationStore`/`SchemaStore`/`AttributeStore` for seeding), `m1/05` (`NpgsqlCteAuthorizer`, `CheckPermissionAsync`, `CteReachability`).

**Pagination contract (spec §7.5).** Conditioned candidates are re-checked and may be dropped after the scan, so storage-level paging alone yields unpredictable page sizes. The contract is **over-fetch and refill**: scan candidates in a deterministic order (ordinal by object id), confirm the permission and conditions per candidate, and return exactly `PageSize` confirmed ids (or fewer only at the true end). The returned `ContinuationToken` is the opaque `ContinuationCursor` encoding the last-confirmed id; a null token means the end of results. This is byte-for-byte the `m0/07` contract; `ListObjects`/`ListSubjects` reuse `ContinuationCursor.Encode`/`DecodeAfter` from `Relkit.Core.Evaluation`.

> **CALIBRATION (critical).** The candidate-generation CTEs are the hardest, least-certain SQL after the check path. The **tests are the spec**: every test here pins behaviour the `m0/07` oracle proves. The SQL is the **approach validated by the `m1/08` differential harness** — candidate generation must never miss a true positive (the confirm-by-Check step removes false positives, never adds them); if `m1/08` finds a missed candidate, widen the CTE, the test is right. `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer` for ListObjects/ListSubjects is the correctness claim, proven by `m1/08`.

---

### Task 1: Reverse-reachability candidate CTE + the type universe

**Files:**
- Create: `src/Relkit.Storage.Postgres/CteCandidates.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Cte/CteCandidatesTests.cs`

**Interfaces:**
- Produces: `CteCandidates` with:
  - `Task<IReadOnlyList<string>> ReachableObjectIdsAsync(NpgsqlConnection conn, TenantContext t, SubjectRef subject, string objectType, CancellationToken ct)` — the distinct, **ordinal-id-sorted** ids of objects of `objectType` reverse-reachable from `subject` (subject's inbound tuples, the inbound tuples of every group it transitively belongs to, and objects reachable by following structural edges). A complete superset of the ListObjects answer; each is confirmed later by Check.
  - `Task<IReadOnlyList<string>> TypeUniverseAsync(NpgsqlConnection conn, TenantContext t, string objectType, CancellationToken ct)` — all object ids of `objectType` appearing as an object in any tuple (covers `type:*` wildcard grants, which reverse traversal does not reach from a concrete subject).
- Consumes: `relation_tuples` (`m1/01`); `SubjectRef`/`TenantContext`.

> **Why reverse reachability + type universe.** Reverse traversal cheaply gathers objects the subject is plausibly connected to but does not by itself respect intersection/exclusion — correctness is the confirm-by-Check step (Task 2). The candidate set must be *complete*: it climbs group membership upward (subject → groups → groups-of-groups) and follows structural edges. The **type universe** is unioned in so a wildcard `viewer@user:*` grant — which no reverse edge from a concrete user reaches — still surfaces every object of the type. This mirrors the oracle's `CandidateObjectsAsync ∪ UniverseOfTypeAsync` (`m0/07`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Cte/CteCandidatesTests.cs
using Relkit.Abstractions;
using Relkit.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteCandidatesTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("cte", "candidates");

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
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("species", "kangaroo"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("species", "wallaby"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("species", "emu"), "editor", new SubjectRef("user", "someone-else")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Reachable_gathers_objects_via_direct_and_nested_group_grants_sorted()
    {
        await using var conn = await fx.OpenAsync();
        var ids = await CteCandidates.ReachableObjectIdsAsync(conn, T, new SubjectRef("user", "alice"), "species");
        ids.ShouldBe(["kangaroo", "wallaby"]);   // sorted, distinct, excludes emu (someone-else's)
    }

    [Fact]
    public async Task Type_universe_returns_every_object_of_the_type()
    {
        await using var conn = await fx.OpenAsync();
        var ids = await CteCandidates.TypeUniverseAsync(conn, T, "species");
        ids.ShouldBe(["emu", "kangaroo", "wallaby"]);   // sorted, all three
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter CteCandidatesTests`
Expected: FAIL — `CteCandidates` does not exist.

- [ ] **Step 3: Implement candidate generation**

> **Calibration:** these CTEs are the candidate validated by the `m1/08` harness. The reverse-reachability CTE climbs the subject → groups graph: the base frontier is the subject (as a leaf and, if it is a group member, as a group-as-member), and the recursive step follows inbound `group#member` tuples upward. From every reached principal it collects inbound tuples whose object is of the target type. Structural edges are followed by a second recursive arm (objects whose tuples point at an already-reached object). Hard-filters `store_id + tenant_id`.

```csharp
// src/Relkit.Storage.Postgres/CteCandidates.cs
using Dapper;
using Npgsql;
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres;

/// <summary>
/// Reverse-reachability candidate generation (the seam decided by m1/02, validated by m1/08).
/// Produces a COMPLETE superset of ListObjects answers; the confirm-by-Check step (m1/06 Task 2)
/// removes false positives. Sorted, distinct, ordinal — so pagination cursors are stable.
/// </summary>
public static class CteCandidates
{
    // Reverse reachability: from the subject, climb nested group membership and follow structural
    // edges, collecting objects of the target type. The `principal` frontier is a (type,id,relation?)
    // triple; relation IS NOT NULL marks a subject-set principal (group-as-member) to climb further.
    private const string ReachableSql = """
        WITH RECURSIVE principals (ptype, pid, prelation) AS (
            -- base: the subject itself (as a plain leaf principal)
            SELECT @stype::text, @sid::text, @srel::text
          UNION
            -- climb: any group whose `member` names a current principal becomes a group-as-member principal
            SELECT rt.object_type, rt.object_id, 'member'
            FROM principals p
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = p.ptype AND rt.subject_id = p.pid
             AND COALESCE(rt.subject_relation, '') = COALESCE(p.prelation, '')
            WHERE rt.object_type = 'group' AND rt.relation = 'member'
        ),
        reached_objects (otype, oid) AS (
            -- objects any principal appears on, plus structural-edge climbing
            SELECT rt.object_type, rt.object_id
            FROM principals p
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = p.ptype AND rt.subject_id = p.pid
             AND COALESCE(rt.subject_relation, '') = COALESCE(p.prelation, '')
          UNION
            -- follow structural edges: an object whose tuple points at an already-reached object
            SELECT rt.object_type, rt.object_id
            FROM reached_objects ro
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.subject_type = ro.otype AND rt.subject_id = ro.oid
             AND rt.subject_relation IS NULL
        )
        SELECT DISTINCT oid
        FROM reached_objects
        WHERE otype = @objtype
        ORDER BY oid
        """;

    private const string UniverseSql = """
        SELECT DISTINCT object_id
        FROM relation_tuples
        WHERE store_id = @store AND tenant_id = @tenant AND object_type = @objtype
        ORDER BY object_id
        """;

    public static async Task<IReadOnlyList<string>> ReachableObjectIdsAsync(
        NpgsqlConnection conn, TenantContext t, SubjectRef subject, string objectType, CancellationToken ct = default)
    {
        var ids = await conn.QueryAsync<string>(new CommandDefinition(ReachableSql,
            new { store = t.Store, tenant = t.Tenant, stype = subject.Type, sid = subject.Id, srel = subject.Relation, objtype = objectType },
            cancellationToken: ct));
        return ids.ToList();
    }

    public static async Task<IReadOnlyList<string>> TypeUniverseAsync(
        NpgsqlConnection conn, TenantContext t, string objectType, CancellationToken ct = default)
    {
        var ids = await conn.QueryAsync<string>(new CommandDefinition(UniverseSql,
            new { store = t.Store, tenant = t.Tenant, objtype = objectType }, cancellationToken: ct));
        return ids.ToList();
    }
}
```

> **Completeness over a deep structural chain.** The `reached_objects` arm follows structural edges one hop at a time and is transitive (it recurses on itself), so a chain `animal#enclosure@enclosure → enclosure#site@site` surfaces the animal when a grant lands on the site. If the `m1/08` harness ever finds a missed candidate through a shape this misses, widen the arm — the test is the spec.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter CteCandidatesTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres tests/Relkit.Storage.Postgres.Tests
git commit -m "feat: add reverse-reachability candidate and type-universe CTEs"
```

---

### Task 2: `ListObjectsAsync` — over-fetch, confirm, refill, paginate

**Files:**
- Create: `src/Relkit.Storage.Postgres/NpgsqlCteAuthorizer.ListObjects.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Cte/CteListObjectsTests.cs`

**Interfaces:**
- Produces: `ListObjectsAsync` (replaces the `m1/05` stub) — builds the candidate set (`ReachableObjectIdsAsync ∪ TypeUniverseAsync`), sorts distinct by ordinal id, skips strictly after the decoded cursor, confirms each via the full pointwise `CheckPermissionAsync`, and returns exactly `PageSize` confirmed ids with a resumable `ContinuationCursor`. Reuses one open connection for candidate generation and all confirms in the page.
- Consumes: `CteCandidates` (Task 1); `CheckPermissionAsync`/`EvalContext`/`SchemaIndex` (`m1/05`); `ContinuationCursor` (`Relkit.Core.Evaluation`, from `m0/07`).

> **Algorithm (mirrors `m0/07` Task 3).** (1) candidates = reverse-reachable ∪ type universe, distinct, ordinal-sorted. (2) skip to strictly after the decoded cursor id. (3) walk candidates; for each, run the full pointwise Check under a **fresh** `EvalContext` (each candidate is an independent membership question; conditions are evaluated). (4) stop once `PageSize` confirm; the token is the last-confirmed id, null only if no confirmable candidate remains beyond it.

- [ ] **Step 1: Write the failing tests** (the `m0/07` ListObjects cases, over Postgres)

```csharp
// tests/Relkit.Storage.Postgres.Tests/Cte/CteListObjectsTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteListObjectsTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(NpgsqlCteAuthorizer Auth, TenantContext T)> SetupAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, Build(), u);
        await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return (new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator()), t);
    }

    private static ListObjectsRequest Req(TenantContext t, string user, int pageSize = 100, string? token = null) => new(
        t, new SubjectRef("user", user), "species", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Lists_only_confirmed_objects_respecting_exclusion()
    {
        var (auth, t) = await SetupAsync("lo-excl",
            Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tup("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            Tup("species", "wallaby", "blocked", new SubjectRef("user", "alice")),
            Tup("group", "macropods", "member", new SubjectRef("user", "alice")));

        var result = await auth.ListObjectsAsync(Req(t, "alice"));
        result.ObjectIds.ShouldBe(["kangaroo"]);   // wallaby excluded
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_lists_every_object_of_the_type()
    {
        var (auth, t) = await SetupAsync("lo-wild",
            Tup("species", "kangaroo", "editor", new SubjectRef("user", "*")),
            Tup("species", "wallaby", "editor", new SubjectRef("user", "*")),
            Tup("species", "emu", "editor", new SubjectRef("user", "*")));

        var result = await auth.ListObjectsAsync(Req(t, "anyone"));
        result.ObjectIds.ShouldBe(["emu", "kangaroo", "wallaby"]);
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var (auth, t) = await SetupAsync("lo-page",
            Tup("species", "a", "editor", new SubjectRef("user", "*")),
            Tup("species", "b", "editor", new SubjectRef("user", "*")),
            Tup("species", "c", "editor", new SubjectRef("user", "*")),
            Tup("species", "d", "editor", new SubjectRef("user", "*")),
            Tup("species", "e", "editor", new SubjectRef("user", "*")));

        var p1 = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2));
        p1.ObjectIds.ShouldBe(["a", "b"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2, token: p1.ContinuationToken));
        p2.ObjectIds.ShouldBe(["c", "d"]);

        var p3 = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2, token: p2.ContinuationToken));
        p3.ObjectIds.ShouldBe(["e"]);
        p3.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pages_do_not_overlap_or_drop_across_the_full_range()
    {
        var (auth, t) = await SetupAsync("lo-range",
            Tup("species", "a", "editor", new SubjectRef("user", "*")),
            Tup("species", "b", "editor", new SubjectRef("user", "*")),
            Tup("species", "c", "editor", new SubjectRef("user", "*")));

        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req(t, "anyone", pageSize: 2, token: token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);

        all.ShouldBe(["a", "b", "c"]);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter CteListObjectsTests`
Expected: FAIL — `ListObjectsAsync` still throws `NotImplementedException` from the `m1/05` stub.

- [ ] **Step 3: Implement `ListObjectsAsync` (remove the stub)**

Delete the `ListObjectsAsync` throwing stub from `NpgsqlCteAuthorizer.Expr.cs` (keep `ListSubjectsAsync` until Task 3), and add:

```csharp
// src/Relkit.Storage.Postgres/NpgsqlCteAuthorizer.ListObjects.cs
using Npgsql;
using Relkit.Abstractions;
using Relkit.Core.Evaluation;

namespace Relkit.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.ObjectType, request.Permission);   // validate: throws on unknown type/permission

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Candidate superset: reverse-reachable ∪ type universe (covers wildcard grants). Both sorted ordinal.
        var reachable = await CteCandidates.ReachableObjectIdsAsync(conn, request.Tenant, request.Subject, request.ObjectType, ct);
        var universe = await CteCandidates.TypeUniverseAsync(conn, request.Tenant, request.ObjectType, ct);
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in reachable) candidates.Add(id);
        foreach (var id in universe) candidates.Add(id);

        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<string>(request.PageSize);
        string? lastConfirmed = null;
        var exhausted = true;

        foreach (var id in candidates)
        {
            if (after is not null && string.CompareOrdinal(id, after) <= 0) continue;   // resume strictly after cursor

            var obj = new EntityRef(request.ObjectType, id);
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, obj, request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(id);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnyConfirmedAfterAsync(conn, index, request, candidates, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListObjectsResult(confirmed, token);
    }

    private async Task<bool> AnyConfirmedAfterAsync(
        NpgsqlConnection conn, SchemaIndex index, ListObjectsRequest request, SortedSet<string> candidates,
        string afterId, CancellationToken ct)
    {
        foreach (var id in candidates)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, new EntityRef(request.ObjectType, id),
                request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter CteListObjectsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres tests/Relkit.Storage.Postgres.Tests
git commit -m "feat: implement CTE list objects with over-fetch/refill pagination"
```

---

### Task 3: `ListSubjectsAsync` — forward-collect leaf users, confirm, paginate

**Files:**
- Create: `src/Relkit.Storage.Postgres/NpgsqlCteAuthorizer.ListSubjects.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Cte/CteListSubjectsTests.cs`

**Interfaces:**
- Produces: `ListSubjectsAsync` (replaces the `m1/05` stub) — forward-collects candidate leaf `user`s from the whole permission expansion (relations, nested groups, arrow targets) via the reachability CTE and recursive C# walk, records whether a `user:*` wildcard appears, then confirms each candidate (including the surfaced `"*"` subject) with the pointwise Check, returning them sorted by id with the same over-fetch/`ContinuationCursor` contract.
- Consumes: `CteReachability.SubjectsThroughRelationAsync`/`EdgesThroughRelationAsync` (`m1/05`); `CheckPermissionAsync`; `ContinuationCursor`; `SchemaIndex`.

> **Approach (mirrors `m0/07` Task 4).** Forward-collect every concrete `user` reachable through the permission's expansion — a superset — then confirm each with Check (so exclusion/intersection are honoured). A `user:*` in any contributing relation surfaces the special `"*"` subject so a public grant is visible; `"*"` (0x2A) sorts first ordinal and flows through the same confirm + paginate loop (no bonus row past `PageSize`). Cycle-guarded.

- [ ] **Step 1: Write the failing tests** (the `m0/07` ListSubjects cases, over Postgres)

```csharp
// tests/Relkit.Storage.Postgres.Tests/Cte/CteListSubjectsTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteListSubjectsTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
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

    private async Task<(NpgsqlCteAuthorizer Auth, TenantContext T)> SetupAsync(string store, params RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, Build(), u);
        await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return (new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator()), t);
    }

    private static ListSubjectsRequest Req(TenantContext t, int pageSize = 100, string? token = null) => new(
        t, new EntityRef("doc", "D1"), "view",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Lists_leaf_users_via_nested_groups_honouring_exclusion()
    {
        var (auth, t) = await SetupAsync("ls-excl",
            Tup("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tup("group", "staff", "member", new SubjectRef("user", "alice")),
            Tup("group", "staff", "member", new SubjectRef("user", "bob")),
            Tup("doc", "D1", "blocked", new SubjectRef("user", "bob")));

        var result = await auth.ListSubjectsAsync(Req(t));
        result.Subjects.Select(s => s.Id).ShouldBe(["alice"]);
    }

    [Fact]
    public async Task Paginates_subjects_to_exact_page_size()
    {
        var (auth, t) = await SetupAsync("ls-page",
            Tup("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tup("group", "staff", "member", new SubjectRef("user", "a")),
            Tup("group", "staff", "member", new SubjectRef("user", "b")),
            Tup("group", "staff", "member", new SubjectRef("user", "c")));

        var p1 = await auth.ListSubjectsAsync(Req(t, pageSize: 2));
        p1.Subjects.Select(s => s.Id).ShouldBe(["a", "b"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListSubjectsAsync(Req(t, pageSize: 2, token: p1.ContinuationToken));
        p2.Subjects.Select(s => s.Id).ShouldBe(["c"]);
        p2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_surfaces_as_star_and_respects_page_size()
    {
        var (auth, t) = await SetupAsync("ls-wild",
            Tup("doc", "D1", "viewer", new SubjectRef("user", "*")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "a")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "b")));

        var p1 = await auth.ListSubjectsAsync(Req(t, pageSize: 2));
        p1.Subjects.Count.ShouldBe(2);
        p1.Subjects.Select(s => s.Id).ShouldBe(["*", "a"]);   // "*" sorts first ordinal
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListSubjectsAsync(Req(t, pageSize: 2, token: p1.ContinuationToken));
        p2.Subjects.Select(s => s.Id).ShouldBe(["b"]);
        p2.ContinuationToken.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter CteListSubjectsTests`
Expected: FAIL — `ListSubjectsAsync` still throws `NotImplementedException`.

- [ ] **Step 3: Implement `ListSubjectsAsync` (remove the stub)**

Delete the `ListSubjectsAsync` throwing stub from `NpgsqlCteAuthorizer.Expr.cs`, and add:

```csharp
// src/Relkit.Storage.Postgres/NpgsqlCteAuthorizer.ListSubjects.cs
using Npgsql;
using Relkit.Abstractions;
using Relkit.Core.Evaluation;

namespace Relkit.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);   // validate

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var candidateUsers = new SortedSet<string>(StringComparer.Ordinal);
        var sawWildcard = new bool[1];
        var visited = new HashSet<EvalFrame>();
        await CollectLeafUsersAsync(conn, index, request.Tenant, request.Object, request.Permission,
            candidateUsers, visited, sawWildcard, ct);
        if (sawWildcard[0]) candidateUsers.Add("*");

        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<SubjectRef>(request.PageSize);
        string? lastConfirmed = null;
        var exhausted = true;

        foreach (var id in candidateUsers)
        {
            if (after is not null && string.CompareOrdinal(id, after) <= 0) continue;
            var subject = new SubjectRef("user", id);
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(subject);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnySubjectConfirmedAfterAsync(conn, index, request, candidateUsers, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListSubjectsResult(confirmed, token);
    }

    private async Task<bool> AnySubjectConfirmedAfterAsync(
        NpgsqlConnection conn, SchemaIndex index, ListSubjectsRequest request, SortedSet<string> users,
        string afterId, CancellationToken ct)
    {
        foreach (var id in users)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                conn, index, request.Tenant, request.Object, request.Permission,
                new SubjectRef("user", id), request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    /// <summary>Forward-collect concrete leaf users (relations, nested groups, arrow targets). Cycle-guarded.</summary>
    private async Task CollectLeafUsersAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SortedSet<string> users, HashSet<EvalFrame> visited, bool[] sawWildcard, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, new SubjectRef("user", "<collect>"));
        if (!visited.Add(frame)) return;
        var def = index.Permission(obj.Type, permission);
        await CollectFromExprAsync(conn, index, tenant, obj, def.Expression, users, visited, sawWildcard, ct);
    }

    private async Task CollectFromExprAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SortedSet<string> users, HashSet<EvalFrame> visited, bool[] sawWildcard, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
                await CollectFromRelationAsync(conn, index, tenant, obj, r.Relation, users, visited, sawWildcard, ct);
                break;
            case Union u:
                await CollectFromExprAsync(conn, index, tenant, obj, u.Left, users, visited, sawWildcard, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, u.Right, users, visited, sawWildcard, ct);
                break;
            case Intersect i:
                await CollectFromExprAsync(conn, index, tenant, obj, i.Left, users, visited, sawWildcard, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, i.Right, users, visited, sawWildcard, ct);
                break;
            case Exclude e:
                await CollectFromExprAsync(conn, index, tenant, obj, e.Left, users, visited, sawWildcard, ct);
                await CollectFromExprAsync(conn, index, tenant, obj, e.Right, users, visited, sawWildcard, ct);
                break;
            case Conditioned c:
                await CollectFromExprAsync(conn, index, tenant, obj, c.Inner, users, visited, sawWildcard, ct);
                break;
            case Arrow a:
            {
                var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, a.Relation, ct);
                foreach (var edge in edges)
                {
                    var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
                    if (index.TryPermission(related.Type, a.Permission, out _))
                        await CollectLeafUsersAsync(conn, index, tenant, related, a.Permission, users, visited, sawWildcard, ct);
                    else
                        await CollectFromRelationAsync(conn, index, tenant, related, a.Permission, users, visited, sawWildcard, ct);
                }
                break;
            }
        }
    }

    private async Task CollectFromRelationAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SortedSet<string> users, HashSet<EvalFrame> visited, bool[] sawWildcard, CancellationToken ct)
    {
        var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, relation, ct);
        foreach (var edge in edges)
        {
            var s = edge.Subject;
            if (s.IsWildcard && string.Equals(s.Type, "user", StringComparison.Ordinal))
                sawWildcard[0] = true;
            else if (!s.IsSubjectSet && string.Equals(s.Type, "user", StringComparison.Ordinal))
                users.Add(s.Id);
            else if (s.IsSubjectSet)
                await CollectFromRelationAsync(conn, index, tenant, new EntityRef(s.Type, s.Id), s.Relation!, users, visited, sawWildcard, ct);
        }
    }
}
```

> `EvalFrame` is in `Relkit.Core.Evaluation` (reused via the `using` above). The collector walks the same shape as the oracle's `m0/07` `CollectLeafUsersAsync`; the only change is reading edges via `CteReachability.EdgesThroughRelationAsync` instead of `IRelationStore.GetByObjectAsync`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter CteListSubjectsTests`
Expected: PASS (3 tests). With Task 2 done, `NpgsqlCteAuthorizer` now implements every `IAuthorizer` member.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Storage.Postgres tests/Relkit.Storage.Postgres.Tests
git commit -m "feat: implement CTE list subjects with leaf-user expansion and pagination"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Reverse-reachability + type-universe CTEs produce a complete, sorted, distinct candidate superset; wildcard grants are covered by the type universe (Task 1).
- [ ] `ListObjects` confirms each candidate via the pointwise CTE Check — exclusion/intersection/conditions honoured; matches the `m0/07` oracle's cases (Task 2).
- [ ] Pagination returns exactly `PageSize` confirmed ids except at the true end; the cursor is the `m0/07` `ContinuationCursor`; no dupes, no gaps across the full range (Tasks 2–3).
- [ ] `ListSubjects` expands nested groups + arrow targets to leaf users, confirms each, and surfaces an unexcluded `user:*` as `"*"` without exceeding `PageSize` (Task 3).
- [ ] Both operations validate the request type/permission (throw `Unknown*Exception` on bad input) (Tasks 2–3).
- [ ] The candidate SQL is framed as "validated by the `m1/08` differential harness," candidate generation never misses a true positive (calibration notes, Task 1).

## Contract gaps (reported, not changed)

- **None new.** `ContinuationCursor` is reused from `Relkit.Core.Evaluation` (`m0/07`), now reachable because `Relkit.Storage.Postgres` references `Relkit.Core` (decided `m1/02`, added `m1/05`). The `m0/07` Contract-gaps note already records that the type universe lives in the provider, not the portable `IRelationStore` — fulfilled here by `CteCandidates.TypeUniverseAsync` (provider-side SQL), exactly as `m0/07` anticipated.
```

