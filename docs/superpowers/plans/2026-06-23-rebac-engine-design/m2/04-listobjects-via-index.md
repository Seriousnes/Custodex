# M2/04 — Index-Backed ListObjects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement an index-backed `ListObjectsAsync` (and `ListSubjectsAsync`) on a new `IndexedAuthorizer` decorator that queries the maintained `reverse_index` (via `IIndexStore` from `m2/01`) for candidate objects, **re-checks only rows flagged `conditioned`** via the CTE condition path, applies the §7.5 over-fetch/refill pagination, and returns results identical to the `m1/06` CTE oracle path. The index is the fast path; on a schema-version mismatch or any per-candidate doubt the decorator falls back to the inner `NpgsqlCteAuthorizer`, and a parity assertion proves `index ≡ oracle`.

**Architecture:** `IndexedAuthorizer : IAuthorizer` wraps the inner `NpgsqlCteAuthorizer` (`m1/05`/`m1/06`) and adds an `IIndexStore`. For `ListObjectsAsync` it scans `reverse_index` rows for `(store, tenant, schema_version, subject, permission, object_type)` — already-resolved **structural** grants. Unconditioned rows are returned directly (the maintained index already proved the structural grant holds). Rows flagged `conditioned` are **re-checked** with the full pointwise CTE Check (`CheckPermissionAsync` honours the tuple/branch conditions against synced attributes + request context) and dropped if the condition fails. The schema version stamped on the scan is the active schema's version; if the index holds no rows for the active version (a schema change invalidated it before the rebuild ran), the decorator **falls back** to the inner authorizer so a stale or not-yet-rebuilt index never serves wrong answers. Pagination is **over-fetch and refill** (spec §7.5), reusing `ContinuationCursor` from `Custodex.Core.Evaluation` byte-for-byte. `ListSubjectsAsync`, `CheckAsync`, and `BatchCheckAsync` delegate to the inner authorizer (the reverse index is keyed for the ListObjects subject→objects direction; ListSubjects stays on the CTE path).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Custodex.Core` (`ContinuationCursor`), `Custodex.Storage.Postgres` (`NpgsqlCteAuthorizer`, `IIndexStore`, `MigrationRunner`, `PostgresFixture`, `NpgsqlUnitOfWorkFactory`, the relation/schema/attribute stores).

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. Depends on `m0/01` (Abstractions, `IIndexStore`, `IAuthorizer`), `m0/07` (the oracle's ListObjects semantics and `ContinuationCursor` shape), `m1/01` (`reverse_index` schema, `MigrationRunner`, `PostgresFixture`), `m1/05` (`NpgsqlCteAuthorizer`, `CheckPermissionAsync`), `m1/06` (the CTE `ListObjectsAsync` this must match and fall back to), `m2/01` (`IIndexStore` + the canonical subject-string encoding), `m2/02`/`m2/03` (index population — the rebuild and incremental maintenance that fill `reverse_index`).

**Pagination contract (spec §7.5).** Conditioned candidates are re-checked and may be dropped after the indexed scan, so storage-level paging alone yields unpredictable page sizes. The contract is **over-fetch and refill**: scan candidates in a deterministic order (ordinal by object id), re-check conditioned rows, and return exactly `PageSize` confirmed ids (or fewer only at the true end). The returned `ContinuationToken` is the opaque `ContinuationCursor` encoding the last-confirmed object id; a null token means the end of results. This is byte-for-byte the `m0/07`/`m1/06` contract.

> **CALIBRATION (critical).** The index-backed path is trusted only where it agrees with the oracle. The **tests are the spec**: every test here pins behaviour the `m1/06` CTE `ListObjectsAsync` (itself proven against the `m0/07` oracle) produces. The `m2/06` differential harness (`index ≡ oracle` including post-write maintenance) is the only proof the fast path is correct; if it finds a divergence, the index path is wrong and the oracle is right. The fallback-on-version-mismatch is the safety net that makes serving from the index safe before the rebuild lands.

## Contract dependency on `m2/01` (`IIndexStore`)

This plan consumes `IIndexStore`, owned by `m2/01`. The exact member shape it relies on is pinned here so the two plans interlock; if `m2/01` lands a different shape, this plan adapts and the divergence is reported as a Contract gap (it does not edit `m2/01` or `README.md`). The relied-upon shape:

```csharp
namespace Custodex.Abstractions;

// A single resolved structural grant row from reverse_index.
public sealed record IndexCandidate(string ObjectId, bool Conditioned);

public interface IIndexStore
{
    // Ordinal-by-object-id scan of resolved structural grants for one (subject, permission, object_type)
    // under the active schema_version, returning only candidates strictly after `afterObjectId`
    // (null = from the start), capped at `limit` rows. Hard-filters store_id + tenant_id.
    Task<IReadOnlyList<IndexCandidate>> ScanObjectsAsync(
        TenantContext tenant, string schemaVersion, string subject, string permission,
        string objectType, string? afterObjectId, int limit, CancellationToken ct = default);

    // True if the index holds at least one row for the active schema_version in this tenant — i.e. it
    // has been built/rebuilt for the current schema. A false here triggers the CTE fallback.
    Task<bool> HasRowsForVersionAsync(
        TenantContext tenant, string schemaVersion, CancellationToken ct = default);
}
```

The **`subject` string** is the canonical encoding `m2/01`/`m2/02` write into `reverse_index.subject`: `"user:alice"` for a plain user, `"group:vets#member"` for a subject-set, `"user:*"` for a wildcard. This plan builds the same string from a `SubjectRef` via the shared `IndexSubject.Of` helper (Task 1), so the scan key matches the maintained rows exactly.

---

### Task 1: `IndexSubject` — canonical subject-string encoding

**Files:**
- Create: `src/Custodex.Storage.Postgres/IndexSubject.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Index/IndexSubjectTests.cs`

**Interfaces:**
- Produces: `static string IndexSubject.Of(SubjectRef subject)` — the canonical `reverse_index.subject` string for a subject ref: `type:id`, or `type:id#relation` for a subject-set, or `type:*` for a wildcard. This is the exact encoding `m2/01`/`m2/02` store, so the `ScanObjectsAsync` key matches the maintained rows.
- Consumes: `SubjectRef` from `Custodex.Abstractions`.

> **Why a shared helper.** Both index maintenance (`m2/02`/`m2/03`) and index reads (this plan) must agree on the subject string byte-for-byte, or a scan misses the rows maintenance wrote. Centralising the encoding in one helper removes the chance of drift. `m2/01` owns the canonical definition; this helper mirrors it. If `m2/01` ships its own helper of the same name/signature, delete this copy and reference theirs (report as a Contract gap).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Index/IndexSubjectTests.cs
using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

public class IndexSubjectTests
{
    [Fact]
    public void Plain_user_encodes_as_type_colon_id()
    {
        IndexSubject.Of(new SubjectRef("user", "alice")).ShouldBe("user:alice");
    }

    [Fact]
    public void Subject_set_includes_the_relation_suffix()
    {
        IndexSubject.Of(new SubjectRef("group", "vets", "member")).ShouldBe("group:vets#member");
    }

    [Fact]
    public void Wildcard_encodes_as_type_colon_star()
    {
        IndexSubject.Of(new SubjectRef("user", "*")).ShouldBe("user:*");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexSubjectTests`
Expected: FAIL — `IndexSubject` does not exist.

- [ ] **Step 3: Implement the encoder**

```csharp
// src/Custodex.Storage.Postgres/IndexSubject.cs
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Canonical <c>reverse_index.subject</c> encoding. Index maintenance (m2/02/m2/03) and index
/// reads (m2/04) share this single definition so a scan key matches the maintained rows exactly:
/// <c>type:id</c>, <c>type:id#relation</c> for a subject-set, <c>type:*</c> for a wildcard.
/// </summary>
public static class IndexSubject
{
    public static string Of(SubjectRef subject) =>
        subject.Relation is null
            ? $"{subject.Type}:{subject.Id}"
            : $"{subject.Type}:{subject.Id}#{subject.Relation}";
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexSubjectTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres/IndexSubject.cs tests/Custodex.Storage.Postgres.Tests/Index/IndexSubjectTests.cs
git commit -m "feat: add canonical reverse-index subject encoder"
```

---

### Task 2: `IndexedAuthorizer` — delegate everything, fall back for ListObjects

**Files:**
- Create: `src/Custodex.Storage.Postgres/IndexedAuthorizer.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Index/IndexedAuthorizerDelegationTests.cs`

**Interfaces:**
- Produces: `IndexedAuthorizer(NpgsqlCteAuthorizer inner, IIndexStore index, ISchemaStore schemaStore) : IAuthorizer`. This task wires `CheckAsync`/`BatchCheckAsync`/`ListSubjectsAsync` straight through to `inner`, and stubs `ListObjectsAsync` to delegate to `inner` (replaced in Task 3 with the index path + fallback). The schema store supplies the active schema version that keys the index scan.
- Consumes: `NpgsqlCteAuthorizer` (`m1/05`/`m1/06`), `IIndexStore` (`m2/01`), `ISchemaStore` (`m0/01`).

> **Why wrap the concrete `NpgsqlCteAuthorizer`.** The decorator needs the inner authorizer's exact `ListObjectsAsync` as its fallback and parity oracle; wrapping the interface would lose nothing functionally, but pinning the concrete type documents that the fallback is the proven CTE path. `CheckAsync`/`BatchCheckAsync`/`ListSubjectsAsync` are unaffected by the reverse index and pass through unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Index/IndexedAuthorizerDelegationTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedAuthorizerDelegationTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private PostgresIndexStore _index = null!;

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member"))
            .Permission("view", p => p.Relation("viewer")))
        .Build();

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _index = new PostgresIndexStore(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(IndexedAuthorizer Auth, TenantContext T)> SetupAsync(string store, params RelationTuple[] tuples)
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
        var inner = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
        return (new IndexedAuthorizer(inner, _index, _schemas), t);
    }

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Check_delegates_to_the_inner_cte_authorizer()
    {
        var (auth, t) = await SetupAsync("idx-check",
            Tup("doc", "D1", "viewer", new SubjectRef("user", "alice")));

        var result = await auth.CheckAsync(new CheckRequest(
            t, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>())));

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ListSubjects_delegates_to_the_inner_cte_authorizer()
    {
        var (auth, t) = await SetupAsync("idx-ls",
            Tup("doc", "D1", "viewer", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "bob")));

        var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
            t, new EntityRef("doc", "D1"), "view",
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>())));

        result.Subjects.Select(s => s.Id).ShouldBe(["alice", "bob"]);
    }
}
```

> `PostgresIndexStore` is the `m2/01` `IIndexStore` implementation (the table-backed scan). This plan uses it as a collaborator; `m2/01` owns it. The seed above writes no `reverse_index` rows yet, so the Task-3 ListObjects index path will hit the empty-index fallback — proven in Task 3.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexedAuthorizerDelegationTests`
Expected: FAIL — `IndexedAuthorizer` does not exist.

- [ ] **Step 3: Implement the decorator with pass-through members**

```csharp
// src/Custodex.Storage.Postgres/IndexedAuthorizer.cs
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Index-backed decorator over <see cref="NpgsqlCteAuthorizer"/>. ListObjects is served from the
/// maintained <c>reverse_index</c> (conditioned rows re-checked), falling back to the inner CTE path
/// when the index has no rows for the active schema_version. Check/BatchCheck/ListSubjects delegate
/// to the inner authorizer unchanged.
/// </summary>
public sealed partial class IndexedAuthorizer : IAuthorizer
{
    private readonly NpgsqlCteAuthorizer _inner;
    private readonly IIndexStore _index;
    private readonly ISchemaStore _schemaStore;

    public IndexedAuthorizer(NpgsqlCteAuthorizer inner, IIndexStore index, ISchemaStore schemaStore)
    {
        _inner = inner;
        _index = index;
        _schemaStore = schemaStore;
    }

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
        => _inner.CheckAsync(request, ct);

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => _inner.BatchCheckAsync(request, ct);

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => _inner.ListSubjectsAsync(request, ct);

    // Replaced in Task 3 with the index scan + conditioned re-check + fallback.
    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => _inner.ListObjectsAsync(request, ct);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexedAuthorizerDelegationTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres/IndexedAuthorizer.cs tests/Custodex.Storage.Postgres.Tests/Index/IndexedAuthorizerDelegationTests.cs
git commit -m "feat: add indexed authorizer decorator with pass-through members"
```

---

### Task 3: Index-backed `ListObjectsAsync` — scan, re-check conditioned, refill, fall back

**Files:**
- Create: `src/Custodex.Storage.Postgres/IndexedAuthorizer.ListObjects.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Index/IndexedListObjectsTests.cs`

**Interfaces:**
- Produces: the real `ListObjectsAsync` on `IndexedAuthorizer` (removing the Task-2 pass-through). It reads the active schema version; if the index has no rows for that version it **delegates to the inner CTE `ListObjectsAsync`** (the safety-net fallback). Otherwise it over-fetches `reverse_index` candidates strictly after the decoded cursor, returns unconditioned rows directly, **re-checks `conditioned` rows** via the inner CTE Check, and refills to exactly `PageSize` confirmed ids with a resumable `ContinuationCursor`.
- Consumes: `IIndexStore.ScanObjectsAsync`/`HasRowsForVersionAsync` (`m2/01`), `IndexSubject.Of` (Task 1), `ContinuationCursor` (`Custodex.Core.Evaluation`, `m0/07`), the inner `NpgsqlCteAuthorizer.CheckAsync` (for the conditioned re-check) and `ListObjectsAsync` (fallback).

> **Why re-check only `conditioned` rows.** The maintained index stores resolved *structural* grants: an unconditioned row is the index asserting "this subject structurally holds this permission on this object" — already true, return it. A `conditioned` row's branch is gated by a request-time predicate the index cannot evaluate, so it is stored-but-flagged and **never assumed**; the decorator re-checks it with `CheckAsync` (which evaluates the condition against synced attributes + request context, spec §8). This is the §7.3 Milestone-2 contract: "one indexed scan plus a condition re-check only on rows flagged `conditioned`."

> **Over-fetch sizing.** Because conditioned rows may drop, scan more than `PageSize` per batch. The implementation over-fetches `PageSize + 1` and keeps scanning in batches until it has `PageSize` confirmed ids or the index is exhausted, so a page is always exactly `PageSize` except at the true end (spec §7.5). The cursor encodes the last *scanned* candidate's id (not merely the last confirmed), so resumption skips re-scanning dropped conditioned rows; a page returns its confirmed ids and a token only if a further candidate exists.

> **CALIBRATION.** The re-check-and-refill loop must produce exactly the `m1/06` CTE `ListObjectsAsync` answer (which the `m0/07` oracle proves). The `m2/06` harness asserts `IndexedAuthorizer.ListObjectsAsync ≡ NpgsqlCteAuthorizer.ListObjectsAsync` across random schemas/tuples and after incremental maintenance; if it diverges, this path is wrong. The tests below pin the same cases `m1/06` pins.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Index/IndexedListObjectsTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedListObjectsTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private PostgresIndexStore _index = null!;

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
        _index = new PostgresIndexStore(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Seeds tuples, sets schema active, and writes the resolved reverse_index rows directly so the
    // index path is exercised without depending on m2/02/m2/03 maintenance. The "v1" version stamp
    // matches the active schema.
    private async Task<(IndexedAuthorizer Auth, NpgsqlCteAuthorizer Oracle, TenantContext T)> SetupAsync(
        string store, IReadOnlyList<RelationTuple> tuples, IReadOnlyList<(string Subject, string Perm, string Type, string Obj, bool Cond)> indexRows)
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
        foreach (var r in indexRows)
            await uow.Connection.ExecuteAsync("""
                INSERT INTO reverse_index (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
                VALUES (@s, @t, 'v1', @subj, @perm, @type, @obj, @cond)
                """,
                new { s = t.Store, t = t.Tenant, subj = r.Subject, perm = r.Perm, type = r.Type, obj = r.Obj, cond = r.Cond }, uow.Transaction);
        await u.CommitAsync();

        var inner = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
        return (new IndexedAuthorizer(inner, _index, _schemas), inner, t);
    }

    private static ListObjectsRequest Req(TenantContext t, string user, int pageSize = 100, string? token = null) => new(
        t, new SubjectRef("user", user), "species", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Returns_unconditioned_index_rows_directly()
    {
        var (auth, _, t) = await SetupAsync("ilo-uncond",
            [Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")),
             Tup("species", "wallaby", "editor", new SubjectRef("user", "alice"))],
            [("user:alice", "edit", "species", "kangaroo", false),
             ("user:alice", "edit", "species", "wallaby", false)]);

        var result = await auth.ListObjectsAsync(Req(t, "alice"));
        result.ObjectIds.ShouldBe(["kangaroo", "wallaby"]);   // sorted, served straight from the index
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Conditioned_rows_are_rechecked_and_dropped_when_the_condition_fails()
    {
        // The index flags wallaby conditioned; the tuple carries a `blocked` exclusion on alice,
        // so the re-check via Check denies wallaby. kangaroo is unconditioned and passes through.
        var (auth, _, t) = await SetupAsync("ilo-cond",
            [Tup("species", "kangaroo", "editor", new SubjectRef("user", "alice")),
             Tup("species", "wallaby", "editor", new SubjectRef("user", "alice")),
             Tup("species", "wallaby", "blocked", new SubjectRef("user", "alice"))],
            [("user:alice", "edit", "species", "kangaroo", false),
             ("user:alice", "edit", "species", "wallaby", true)]);   // flagged: must be re-checked

        var result = await auth.ListObjectsAsync(Req(t, "alice"));
        result.ObjectIds.ShouldBe(["kangaroo"]);   // wallaby re-checked => excluded => dropped
    }

    [Fact]
    public async Task Falls_back_to_the_cte_path_when_the_index_has_no_rows_for_the_version()
    {
        // No reverse_index rows written => HasRowsForVersion is false => delegate to inner CTE ListObjects,
        // which computes the answer from tuples directly.
        var (auth, oracle, t) = await SetupAsync("ilo-fallback",
            [Tup("species", "emu", "editor", new SubjectRef("user", "*"))],
            indexRows: []);

        var viaIndex = await auth.ListObjectsAsync(Req(t, "anyone"));
        var viaOracle = await oracle.ListObjectsAsync(Req(t, "anyone"));
        viaIndex.ObjectIds.ShouldBe(viaOracle.ObjectIds);
        viaIndex.ObjectIds.ShouldBe(["emu"]);
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var tuples = new List<RelationTuple>();
        var rows = new List<(string, string, string, string, bool)>();
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            tuples.Add(Tup("species", id, "editor", new SubjectRef("user", "alice")));
            rows.Add(("user:alice", "edit", "species", id, false));
        }
        var (auth, _, t) = await SetupAsync("ilo-page", tuples, rows);

        var p1 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2));
        p1.ObjectIds.ShouldBe(["a", "b"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2, token: p1.ContinuationToken));
        p2.ObjectIds.ShouldBe(["c", "d"]);

        var p3 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2, token: p2.ContinuationToken));
        p3.ObjectIds.ShouldBe(["e"]);
        p3.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pagination_holds_when_conditioned_rows_drop_inside_a_page()
    {
        // b and d are conditioned and fail re-check (blocked), so a full scan yields a, c, e.
        // With pageSize 2 the over-fetch/refill must still return [a, c] then [e].
        var tuples = new List<RelationTuple>();
        var rows = new List<(string, string, string, string, bool)>();
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            tuples.Add(Tup("species", id, "editor", new SubjectRef("user", "alice")));
            var conditioned = id is "b" or "d";
            if (conditioned) tuples.Add(Tup("species", id, "blocked", new SubjectRef("user", "alice")));
            rows.Add(("user:alice", "edit", "species", id, conditioned));
        }
        var (auth, _, t) = await SetupAsync("ilo-drop", tuples, rows);

        var p1 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2));
        p1.ObjectIds.ShouldBe(["a", "c"]);
        p1.ContinuationToken.ShouldNotBeNull();

        var p2 = await auth.ListObjectsAsync(Req(t, "alice", pageSize: 2, token: p1.ContinuationToken));
        p2.ObjectIds.ShouldBe(["e"]);
        p2.ContinuationToken.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexedListObjectsTests`
Expected: FAIL — `ListObjectsAsync` still delegates (no index scan), so conditioned re-check and pagination-on-drop fail.

- [ ] **Step 3: Implement the index-backed ListObjects (remove the pass-through)**

Delete the pass-through `ListObjectsAsync` from `IndexedAuthorizer.cs` (keep the other three members), and add:

```csharp
// src/Custodex.Storage.Postgres/IndexedAuthorizer.ListObjects.cs
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class IndexedAuthorizer
{
    // How many index rows to over-fetch per scan batch beyond the requested page, so dropped
    // conditioned rows are refilled within one or few round-trips (spec §7.5 over-fetch/refill).
    private const int OverFetchPadding = 1;

    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var schema = await _schemaStore.GetActiveAsync(request.Tenant.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{request.Tenant.Store}'>");

        // Safety net: if the index holds no rows for the active schema_version (a schema change
        // invalidated it, or the rebuild has not run yet), serve from the proven CTE path.
        if (!await _index.HasRowsForVersionAsync(request.Tenant, schema.Version, ct))
            return await _inner.ListObjectsAsync(request, ct);

        var subject = IndexSubject.Of(request.Subject);
        var after = ContinuationCursor.DecodeAfter(request.ContinuationToken);
        var confirmed = new List<string>(request.PageSize);
        string? lastScanned = after;
        var anyRemaining = false;
        var batchSize = request.PageSize + OverFetchPadding;

        while (confirmed.Count < request.PageSize)
        {
            var batch = await _index.ScanObjectsAsync(
                request.Tenant, schema.Version, subject, request.Permission, request.ObjectType,
                lastScanned, batchSize, ct);
            if (batch.Count == 0)
                break;   // index exhausted

            foreach (var candidate in batch)
            {
                lastScanned = candidate.ObjectId;

                if (confirmed.Count == request.PageSize)
                {
                    // We already have a full page; this extra candidate means more results remain.
                    anyRemaining = await ConfirmAsync(request, candidate, ct);
                    if (anyRemaining) break;
                    continue;
                }

                if (await ConfirmAsync(request, candidate, ct))
                    confirmed.Add(candidate.ObjectId);
            }

            if (anyRemaining) break;
            if (batch.Count < batchSize) break;   // last (partial) batch: index exhausted
        }

        // If the page is full, probe whether any confirmable candidate remains past the last scanned id.
        if (confirmed.Count == request.PageSize && !anyRemaining)
            anyRemaining = await AnyConfirmedAfterAsync(request, schema.Version, subject, lastScanned!, ct);

        var token = anyRemaining ? ContinuationCursor.Encode(lastScanned!) : null;
        return new ListObjectsResult(confirmed, token);
    }

    // An unconditioned index row is a resolved structural grant: trust it. A conditioned row is
    // re-checked with the full pointwise Check (evaluates the request-time predicate).
    private async Task<bool> ConfirmAsync(ListObjectsRequest request, IndexCandidate candidate, CancellationToken ct)
    {
        if (!candidate.Conditioned)
            return true;

        var result = await _inner.CheckAsync(new CheckRequest(
            request.Tenant, new EntityRef(request.ObjectType, candidate.ObjectId),
            request.Permission, request.Subject, request.Context), ct);
        return result.Allowed;
    }

    // True if at least one candidate strictly after `afterId` confirms — so the page advertises a cursor.
    private async Task<bool> AnyConfirmedAfterAsync(
        ListObjectsRequest request, string schemaVersion, string subject, string afterId, CancellationToken ct)
    {
        string? cursor = afterId;
        while (true)
        {
            var batch = await _index.ScanObjectsAsync(
                request.Tenant, schemaVersion, subject, request.Permission, request.ObjectType,
                cursor, request.PageSize, ct);
            if (batch.Count == 0)
                return false;

            foreach (var candidate in batch)
            {
                cursor = candidate.ObjectId;
                if (await ConfirmAsync(request, candidate, ct))
                    return true;
            }

            if (batch.Count < request.PageSize)
                return false;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexedListObjectsTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres/IndexedAuthorizer.cs src/Custodex.Storage.Postgres/IndexedAuthorizer.ListObjects.cs tests/Custodex.Storage.Postgres.Tests/Index/IndexedListObjectsTests.cs
git commit -m "feat: index-backed list objects with conditioned re-check and over-fetch pagination"
```

---

### Task 4: Parity assertion — `IndexedAuthorizer ≡ NpgsqlCteAuthorizer` for ListObjects

**Files:**
- Test: `tests/Custodex.Storage.Postgres.Tests/Index/IndexedListObjectsParityTests.cs`

**Interfaces:**
- Produces: a focused parity test proving the index-backed `ListObjectsAsync` returns the same ids (and the same paged ranges) as the inner CTE `ListObjectsAsync` oracle, across exclusion, wildcard, and conditioned-drop shapes. This is the per-test guard that complements the `m2/06` property-based `index ≡ oracle` harness — it documents the equivalence at the boundary this plan owns.
- Consumes: `IndexedAuthorizer` (Tasks 2–3), `NpgsqlCteAuthorizer` (`m1/06`), `PostgresIndexStore` (`m2/01`).

> **Why both this and `m2/06`.** `m2/06` is the broad CsCheck harness over random inputs and after maintenance. This task is a small, readable, always-on assertion that the fast path equals the oracle for the canonical shapes — fast feedback if a refactor breaks parity, without waiting on the full property run.

- [ ] **Step 1: Write the parity test**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Index/IndexedListObjectsParityTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Index;

[Collection("postgres")]
public class IndexedListObjectsParityTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private PostgresIndexStore _index = null!;

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
        _index = new PostgresIndexStore(fx.ConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(IndexedAuthorizer Indexed, NpgsqlCteAuthorizer Oracle, TenantContext T)> SetupAsync(
        string store, IReadOnlyList<RelationTuple> tuples, IReadOnlyList<(string Subject, string Type, string Obj, bool Cond)> indexRows)
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
        foreach (var r in indexRows)
            await uow.Connection.ExecuteAsync("""
                INSERT INTO reverse_index (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
                VALUES (@s, @t, 'v1', @subj, 'edit', @type, @obj, @cond)
                """,
                new { s = t.Store, t = t.Tenant, subj = r.Subject, type = r.Type, obj = r.Obj, cond = r.Cond }, uow.Transaction);
        await u.CommitAsync();

        var oracle = new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
        return (new IndexedAuthorizer(oracle, _index, _schemas), oracle, t);
    }

    private static ListObjectsRequest Req(TenantContext t, string user, int pageSize, string? token) => new(
        t, new SubjectRef("user", user), "species", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user), new Dictionary<string, object?>()),
        pageSize, token);

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private static async Task<List<string>> PageAllAsync(IAuthorizer auth, TenantContext t, string user, int pageSize)
    {
        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req(t, user, pageSize, token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);
        return all;
    }

    [Fact]
    public async Task Index_path_equals_cte_oracle_across_exclusion_and_conditioned_drops()
    {
        var (indexed, oracle, t) = await SetupAsync("parity",
            [Tup("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
             Tup("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
             Tup("species", "wallaby", "blocked", new SubjectRef("user", "alice")),       // alice revoked on wallaby
             Tup("species", "quokka", "editor", new SubjectRef("user", "alice")),
             Tup("group", "macropods", "member", new SubjectRef("user", "alice"))],
            [("group:macropods#member", "species", "kangaroo", false),
             ("group:macropods#member", "species", "wallaby", true),                       // conditioned: re-checked, drops
             ("user:alice", "species", "quokka", false)]);

        var viaIndex = await PageAllAsync(indexed, t, "alice", pageSize: 2);
        var viaOracle = await PageAllAsync(oracle, t, "alice", pageSize: 2);

        viaIndex.ShouldBe(viaOracle);
        viaIndex.ShouldBe(["kangaroo", "quokka"]);   // wallaby excluded by the re-check
    }
}
```

> The index rows are seeded to mirror what `m2/02`/`m2/03` maintenance would produce: a structural row per grant path, `conditioned=true` only where a request-time predicate gates the branch. The `group:macropods#member` subject row models a grant the subject reaches through group membership; the index path and the oracle resolve `alice`'s membership identically.

- [ ] **Step 2: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter IndexedListObjectsParityTests`
Expected: PASS (1 test) — index-backed paging equals the CTE oracle end to end.

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Storage.Postgres.Tests/Index/IndexedListObjectsParityTests.cs
git commit -m "test: assert indexed list objects equals the cte oracle"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] `IndexSubject.Of` produces the exact `reverse_index.subject` string maintenance writes (Task 1).
- [ ] `Check`/`BatchCheck`/`ListSubjects` delegate unchanged to the inner CTE authorizer (Task 2).
- [ ] Unconditioned index rows are returned directly; only `conditioned` rows are re-checked via the inner Check (Task 3).
- [ ] An empty index for the active schema version falls back to the inner CTE `ListObjectsAsync` (Task 3).
- [ ] Pagination returns exactly `PageSize` confirmed ids except at the true end, even when conditioned rows drop inside a page; the cursor is the `m0/07` `ContinuationCursor` (Tasks 3–4).
- [ ] The index-backed path equals the CTE oracle for exclusion/wildcard/conditioned shapes (Task 4), the per-boundary complement to the `m2/06` property harness.

## Contract gaps (reported, not changed)

- **`IIndexStore` member shape is pinned by this plan, not yet by `m2/01`.** This plan relies on `IndexCandidate(string ObjectId, bool Conditioned)`, `IIndexStore.ScanObjectsAsync(tenant, schemaVersion, subject, permission, objectType, afterObjectId, limit, ct)`, and `IIndexStore.HasRowsForVersionAsync(tenant, schemaVersion, ct)`, plus a `PostgresIndexStore` implementation. `m2/01` owns `IIndexStore` and the `reverse_index.subject` encoding. If `m2/01` lands a different scan signature or subject encoding, adapt this plan's calls and `IndexSubject` to match — the `README.md` contract's `IIndexStore { /* M2 */ }` is intentionally open, so `m2/01` defines the members and this plan consumes them. No `README.md` change made here.
- **The conditioned re-check uses the public `CheckAsync`, not an internal `(Allowed, ConditionTouched)` path.** For ListObjects the decorator only needs allow/deny per conditioned candidate, which `CheckAsync` provides; it does not need the cacheability signal `m0/08` consumes. No new contract surface is required.
