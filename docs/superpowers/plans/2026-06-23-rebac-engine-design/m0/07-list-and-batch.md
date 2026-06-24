# M0/07 — Engine-Driven ListObjects, ListSubjects & BatchCheck Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `EngineDrivenAuthorizer` (from `m0/05`) with engine-driven `ListObjectsAsync` (the correctness oracle), `ListSubjectsAsync`, and `BatchCheckAsync`, plus the over-fetch/refill pagination contract with an opaque, deterministic continuation cursor.

**Architecture:** `ListObjects` is the **oracle**: reverse-traverse from the subject (tuples where the subject, or a group it transitively belongs to, appears), walk forward to candidate objects of the target type, then **confirm each candidate with the same pointwise `CheckAsync`** that `m0/05` proved — including conditions. It is "always correct, heavier when access is broad," exactly as spec §7.3 Milestone 1 describes. `ListSubjects` forward-expands the permission tree down to leaf `user`s. `BatchCheck` runs many items sharing one per-request memo. Pagination uses a deterministic ordering (by object id, ordinal) so a cursor can encode the last-returned id and resume.

**Tech Stack:** .NET 10, C# 14, xUnit, Shouldly. Uses `Custodex.Storage.InMemory` (from `m0/04`).

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. Depends on `m0/05` (the `EngineDrivenAuthorizer` and pointwise Check, including `CheckPermissionAsync`, `ResolveRelationAsync`, `EvalContext`, `SchemaIndex`).

**Pagination contract (spec §7.5).** Conditioned candidates are re-checked and may be dropped after the scan, so storage-level paging alone yields unpredictable page sizes. The contract is **over-fetch and refill**: scan candidates in a deterministic order, confirm the permission (and conditions) per candidate, and return exactly `PageSize` confirmed ids (or fewer only at the true end). The returned `ContinuationToken` is an opaque cursor encoding the last-confirmed object id; callers page by passing it back. A null token means the end of results.

---

### Task 1: Deterministic candidate enumeration — reverse reachability

**Files:**
- Create: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Reverse.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/ReverseReachabilityTests.cs`

**Interfaces:**
- Produces: a private `Task<IReadOnlyList<EntityRef>> CandidateObjectsAsync(SchemaIndex index, TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct)` returning the **distinct, ordinal-id-sorted** set of objects of `objectType` reachable from `subject` by reverse traversal (subject's direct tuples, plus tuples of every group the subject transitively belongs to, plus objects reachable by following structural-reference edges). This is a *superset* of the answer — `ListObjects` confirms each with a full Check. Cycle-guarded over the group/edge graph.
- Consumes: `IRelationStore.GetBySubjectAsync` (from `Custodex.Abstractions`), `SchemaIndex`, `EntityRef`, `SubjectRef`.

> **Why a superset, then confirm.** Reverse traversal cheaply gathers "objects this subject is plausibly connected to"; it does not by itself respect intersection/exclusion. Correctness comes from re-checking each candidate with the pointwise Check from `m0/05`. The candidate set must be *complete* (never miss a true positive) — so it follows group membership upward (subject → groups → groups-of-groups) and structural edges. Determinism (sorted, distinct) is what makes pagination cursors stable.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/ReverseReachabilityTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class ReverseReachabilityTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor")))
        .Build();

    private static async Task<(EngineDrivenAuthorizer Auth, InMemoryRelationStore Rel)> NewAsync(params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, Build(), uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return (new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator()), relations);
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Gathers_objects_reachable_via_direct_and_nested_group_grants()
    {
        var (auth, _) = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "emu", "editor", new SubjectRef("user", "someone-else")),
            Tuple("group", "macropods", "member", new SubjectRef("user", "alice")));

        var candidates = await auth.CandidateObjectsForTest(T, new SubjectRef("user", "alice"), "species");
        candidates.Select(c => c.Id).ShouldBe(new[] { "kangaroo", "wallaby" });   // sorted, distinct, excludes emu
    }

    [Fact]
    public async Task Candidate_enumeration_is_cycle_safe()
    {
        var (auth, _) = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("group", "a", "member")),
            Tuple("group", "a", "member", new SubjectRef("group", "b", "member")),
            Tuple("group", "b", "member", new SubjectRef("group", "a", "member")),
            Tuple("group", "a", "member", new SubjectRef("user", "alice")));

        var candidates = await auth.CandidateObjectsForTest(T, new SubjectRef("user", "alice"), "species");
        candidates.Select(c => c.Id).ShouldBe(new[] { "kangaroo" });
    }
}
```

> The test calls a thin `internal` test seam `CandidateObjectsForTest`. Add it in Step 3 forwarding to the real private method, marked `[InternalsVisibleTo("Custodex.Core.Tests")]` (already configured in `m0/02` or add it here).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter ReverseReachabilityTests`
Expected: FAIL — `CandidateObjectsForTest` not defined.

- [ ] **Step 3: Implement reverse reachability**

If not already present (from `m0/02`), expose internals to the test project. Add to `src/Custodex.Core/Custodex.Core.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Custodex.Core.Tests" />
</ItemGroup>
```

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Reverse.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    internal Task<IReadOnlyList<EntityRef>> CandidateObjectsForTest(
        TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct = default)
        => CandidateObjectsAsync(tenant, subject, objectType, ct);

    /// <summary>
    /// Reverse traversal: the distinct, ordinal-id-sorted objects of
    /// <paramref name="objectType"/> reachable from <paramref name="subject"/> by
    /// following the subject's tuples and the tuples of every group it transitively
    /// belongs to, plus structural-reference edges into objects of the target type.
    /// A complete superset of the ListObjects answer; each is later confirmed by Check.
    /// </summary>
    private async Task<IReadOnlyList<EntityRef>> CandidateObjectsAsync(
        TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var visitedSubjects = new HashSet<SubjectRef>();
        var visitedObjects = new HashSet<EntityRef>();

        // Frontier of "principals" whose inbound tuples we follow: the subject and
        // every group-as-member it expands into.
        var subjectQueue = new Queue<SubjectRef>();
        subjectQueue.Enqueue(subject);

        // Objects discovered via structural edges that we still need to walk through.
        var objectQueue = new Queue<EntityRef>();

        while (subjectQueue.Count > 0)
        {
            var principal = subjectQueue.Dequeue();
            if (!visitedSubjects.Add(principal)) continue;

            var inbound = await _relations.GetBySubjectAsync(tenant, principal, ct);
            foreach (var tuple in inbound)
            {
                var obj = tuple.Object;

                if (string.Equals(obj.Type, objectType, StringComparison.Ordinal))
                    found.Add(obj.Id);

                // A group whose membership names this principal => climb to the group-as-member.
                if (string.Equals(obj.Type, "group", StringComparison.Ordinal)
                    && string.Equals(tuple.Relation, "member", StringComparison.Ordinal))
                {
                    subjectQueue.Enqueue(new SubjectRef(obj.Type, obj.Id, "member"));
                }

                // Any object reached is a potential start for structural-edge walks.
                if (visitedObjects.Add(obj)) objectQueue.Enqueue(obj);
            }
        }

        // Walk structural edges (e.g. enclosure -> animal) so arrow-inherited grants
        // surface candidates of the target type. We discover objects whose tuples point
        // *at* an already-found object, climbing one structural hop at a time.
        while (objectQueue.Count > 0)
        {
            var obj = objectQueue.Dequeue();
            var asSubject = new SubjectRef(obj.Type, obj.Id);
            if (!visitedSubjects.Add(asSubject)) continue;

            var inbound = await _relations.GetBySubjectAsync(tenant, asSubject, ct);
            foreach (var tuple in inbound)
            {
                var owner = tuple.Object;
                if (string.Equals(owner.Type, objectType, StringComparison.Ordinal))
                    found.Add(owner.Id);
                if (visitedObjects.Add(owner)) objectQueue.Enqueue(owner);
            }
        }

        return found.Select(id => new EntityRef(objectType, id)).ToList();
    }
}
```

> **Calibration note.** Candidate generation must never miss a true positive; the confirm-by-Check step removes false positives. If a worked example or the M1 differential harness later finds a missed candidate (e.g. a deeper structural chain), widen this traversal — the *tests* are the spec, this BFS is the candidate-completeness approach to validate. Wildcard `type:*` grants are handled in Task 2's confirm step (a wildcard makes every object of the type a candidate), not here.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter ReverseReachabilityTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Reverse.cs src/Custodex.Core/Custodex.Core.csproj tests/Custodex.Core.Tests/Evaluation/ReverseReachabilityTests.cs
git commit -m "feat: add reverse candidate enumeration for list objects"
```

---

### Task 2: Pagination cursor + confirm loop

**Files:**
- Create: `src/Custodex.Core/Evaluation/ContinuationCursor.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/ContinuationCursorTests.cs`

**Interfaces:**
- Produces: `ContinuationCursor` with `static string Encode(string lastObjectId)` and `static string? DecodeAfter(string? token)` (returns the id to resume strictly after, or null for "from the start"). Opaque (Base64Url of the id) and deterministic.
- Consumes: nothing beyond BCL.

> Wildcard grants need full enumeration of the target type. The full type universe is the union of candidate ids and the ids of all objects of that type that the subject could reach via a `type:*` tuple. For the in-memory oracle we keep it simple and correct: a wildcard tuple on an object means that object is a candidate; the universe of objects of a type is whatever appears as an object in any tuple of that type. That set is gathered in Task 3.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/ContinuationCursorTests.cs
using Custodex.Core.Evaluation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class ContinuationCursorTests
{
    [Fact]
    public void Encode_then_decode_round_trips_the_last_id()
    {
        var token = ContinuationCursor.Encode("wallaby");
        token.ShouldNotBe("wallaby");                 // opaque, not the raw id
        ContinuationCursor.DecodeAfter(token).ShouldBe("wallaby");
    }

    [Fact]
    public void Null_or_empty_token_decodes_to_null_meaning_start()
    {
        ContinuationCursor.DecodeAfter(null).ShouldBeNull();
        ContinuationCursor.DecodeAfter("").ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter ContinuationCursorTests`
Expected: FAIL — `ContinuationCursor` not defined.

- [ ] **Step 3: Implement the cursor**

```csharp
// src/Custodex.Core/Evaluation/ContinuationCursor.cs
using System.Buffers.Text;
using System.Text;

namespace Custodex.Core.Evaluation;

/// <summary>
/// Opaque, deterministic pagination cursor over a stable object-id ordering.
/// Encodes the last-returned object id; callers resume strictly after it.
/// </summary>
public static class ContinuationCursor
{
    public static string Encode(string lastObjectId)
    {
        var bytes = Encoding.UTF8.GetBytes(lastObjectId);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? DecodeAfter(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var padded = token.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter ContinuationCursorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/ContinuationCursor.cs tests/Custodex.Core.Tests/Evaluation/ContinuationCursorTests.cs
git commit -m "feat: add opaque deterministic pagination cursor"
```

---

### Task 3: `ListObjectsAsync` — over-fetch, confirm, refill, paginate

**Files:**
- Create: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListObjects.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/ListObjectsTests.cs`

**Interfaces:**
- Produces: `ListObjectsAsync` (replaces the `m0/05` stub) confirming each candidate via the pointwise Check and honouring the over-fetch/refill pagination contract. Adds a private `Task<IReadOnlyList<EntityRef>> UniverseOfTypeAsync(...)` gathering all objects of the target type that appear in any tuple (for wildcard grants).
- Consumes: `CandidateObjectsAsync` (Task 1), `ContinuationCursor` (Task 2), `CheckPermissionAsync`/`EvalContext` (m0/05), `IRelationStore`.

> **Algorithm.** (1) Build the full candidate set: reverse-reachable candidates ∪ the type universe (so `type:*` wildcard grants are not missed). Sort distinct by ordinal id. (2) Skip to strictly after the decoded cursor id. (3) Walk candidates in order; for each, run the **full pointwise Check** (`CheckPermissionAsync`, which evaluates conditions) under a fresh `EvalContext`; keep confirmed ids. (4) Stop once `PageSize` are confirmed; the continuation token is the last confirmed id (null if the walk reached the end). Each candidate is confirmed independently, so a page is always exactly `PageSize` unless the candidates are exhausted.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/ListObjectsTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class ListObjectsTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Build();

    private static async Task<EngineDrivenAuthorizer> NewAsync(params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, Build(), uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static ListObjectsRequest Req(string user, int pageSize = 100, string? token = null) => new(
        T, new SubjectRef("user", user), "species", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()), pageSize, token);

    [Fact]
    public async Task Lists_only_confirmed_objects_respecting_exclusion()
    {
        var auth = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "wallaby", "blocked", new SubjectRef("user", "alice")),     // alice revoked on wallaby
            Tuple("group", "macropods", "member", new SubjectRef("user", "alice")));

        var result = await auth.ListObjectsAsync(Req("alice"));
        result.ObjectIds.ShouldBe(new[] { "kangaroo" });   // wallaby excluded
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_lists_every_object_of_the_type()
    {
        var auth = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("user", "*")),
            Tuple("species", "wallaby", "editor", new SubjectRef("user", "*")),
            Tuple("species", "emu", "editor", new SubjectRef("user", "*")));

        var result = await auth.ListObjectsAsync(Req("anyone"));
        result.ObjectIds.ShouldBe(new[] { "emu", "kangaroo", "wallaby" });   // sorted, all three
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var auth = await NewAsync(
            Tuple("species", "a", "editor", new SubjectRef("user", "*")),
            Tuple("species", "b", "editor", new SubjectRef("user", "*")),
            Tuple("species", "c", "editor", new SubjectRef("user", "*")),
            Tuple("species", "d", "editor", new SubjectRef("user", "*")),
            Tuple("species", "e", "editor", new SubjectRef("user", "*")));

        var page1 = await auth.ListObjectsAsync(Req("anyone", pageSize: 2));
        page1.ObjectIds.ShouldBe(new[] { "a", "b" });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListObjectsAsync(Req("anyone", pageSize: 2, token: page1.ContinuationToken));
        page2.ObjectIds.ShouldBe(new[] { "c", "d" });
        page2.ContinuationToken.ShouldNotBeNull();

        var page3 = await auth.ListObjectsAsync(Req("anyone", pageSize: 2, token: page2.ContinuationToken));
        page3.ObjectIds.ShouldBe(new[] { "e" });
        page3.ContinuationToken.ShouldBeNull();   // true end of results
    }

    [Fact]
    public async Task Pages_do_not_overlap_or_drop_across_the_full_range()
    {
        var auth = await NewAsync(
            Tuple("species", "a", "editor", new SubjectRef("user", "*")),
            Tuple("species", "b", "editor", new SubjectRef("user", "*")),
            Tuple("species", "c", "editor", new SubjectRef("user", "*")));

        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req("anyone", pageSize: 2, token: token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);

        all.ShouldBe(new[] { "a", "b", "c" });   // no dupes, no gaps
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter ListObjectsTests`
Expected: FAIL — `ListObjectsAsync` still throws `NotImplementedException` from the `m0/05` stub.

- [ ] **Step 3: Implement `ListObjectsAsync` (remove the stub)**

Delete the `ListObjectsAsync` stub from `EngineDrivenAuthorizer.List.cs` (leave `BatchCheckAsync`/`ListSubjectsAsync` stubs in place for now), and add:

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListObjects.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public async Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.ObjectType, request.Permission);   // validate: throws on unknown type/permission

        // Candidate superset: reverse-reachable ∪ type universe (covers wildcard grants).
        var reachable = await CandidateObjectsAsync(request.Tenant, request.Subject, request.ObjectType, ct);
        var universe = await UniverseOfTypeAsync(request.Tenant, request.ObjectType, ct);

        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in reachable) candidates.Add(r.Id);
        foreach (var u in universe) candidates.Add(u.Id);

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
                index, request.Tenant, obj, request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(id);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                // Determine whether any confirmable candidate remains beyond this page.
                exhausted = !await AnyConfirmedAfterAsync(index, request, candidates, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListObjectsResult(confirmed, token);
    }

    /// <summary>True if at least one candidate strictly after <paramref name="afterId"/> confirms the permission.</summary>
    private async Task<bool> AnyConfirmedAfterAsync(
        SchemaIndex index, ListObjectsRequest request, SortedSet<string> candidates, string afterId, CancellationToken ct)
    {
        foreach (var id in candidates)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                index, request.Tenant, new EntityRef(request.ObjectType, id),
                request.Permission, request.Subject, request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    /// <summary>All objects of <paramref name="objectType"/> appearing as an object in any tuple (the type universe).</summary>
    private async Task<IReadOnlyList<EntityRef>> UniverseOfTypeAsync(
        TenantContext tenant, string objectType, CancellationToken ct)
    {
        // The in-memory store exposes a forward scan by type for the oracle path.
        var objects = await _relations.GetObjectsOfTypeAsync(tenant, objectType, ct);
        return objects;
    }
}
```

> This needs one extra read primitive on the in-memory store: `GetObjectsOfTypeAsync(TenantContext, string objectType, CancellationToken)` returning the distinct objects of that type. It is an in-memory-provider convenience for the oracle (not part of the portable `IRelationStore` contract). Add it to `InMemoryRelationStore` (Step 3b).

- [ ] **Step 3b: Add `GetObjectsOfTypeAsync` to the in-memory store**

```csharp
// Add to src/Custodex.Storage.InMemory/InMemoryRelationStore.cs
// (alongside the existing GetByObjectAsync/GetBySubjectAsync/WriteAsync members)

    /// <summary>Oracle helper: distinct objects of a type that appear in any tuple of this tenant.</summary>
    public Task<IReadOnlyList<EntityRef>> GetObjectsOfTypeAsync(
        TenantContext t, string objectType, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<EntityRef> result =
                _data.TryGetValue(KeyOf(t), out var bucket)
                    ? bucket.Values
                        .Where(x => string.Equals(x.Object.Type, objectType, StringComparison.Ordinal))
                        .Select(x => x.Object)
                        .Distinct()
                        .ToList()
                    : [];
            return Task.FromResult(result);
        }
    }
```

> This uses the actual `m0/04` `InMemoryRelationStore` internals: the `_gate` lock, the `_data` dictionary (`Dictionary<Key, Dictionary<TupleIdentity, RelationTuple>>`), and the private `KeyOf(TenantContext)` helper. The intent is a tenant-scoped, distinct, forward scan by object type. If `m0/04`'s field shape changes, adapt the projection to match.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter ListObjectsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListObjects.cs src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.List.cs src/Custodex.Storage.InMemory tests/Custodex.Core.Tests/Evaluation/ListObjectsTests.cs
git commit -m "feat: implement engine-driven list objects with over-fetch pagination"
```

---

### Task 4: `ListSubjectsAsync` — forward-expand to leaf users

**Files:**
- Create: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListSubjects.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/ListSubjectsTests.cs`

**Interfaces:**
- Produces: `ListSubjectsAsync` (replaces the `m0/05` stub) forward-expanding the permission tree from the object down to leaf `user` subjects, then confirming each leaf user with the pointwise Check (so exclusions/intersections are honoured), returning them sorted by id with the same cursor contract.
- Consumes: `SchemaIndex`, the permission AST walk, `IRelationStore.GetByObjectAsync`, `CheckPermissionAsync`, `ContinuationCursor`.

> **Approach.** Forward-collect every `user` that appears anywhere in the permission's expansion (relations, nested groups, arrow targets) — a candidate superset of leaf users — then **confirm each with Check**, exactly mirroring ListObjects. A wildcard `user:*` in any contributing relation means "every user"; for the oracle we surface the concrete users discovered plus, when a wildcard is present and not excluded, the special wildcard subject `user:*` so callers can detect a public grant. Confirmed users are sorted by id; pagination uses `ContinuationCursor`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/ListSubjectsTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class ListSubjectsTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
        .Build();

    private static async Task<EngineDrivenAuthorizer> NewAsync(params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, Build(), uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static ListSubjectsRequest Req(int pageSize = 100, string? token = null) => new(
        T, new EntityRef("doc", "D1"), "view",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"),
            new Dictionary<string, object?>()), pageSize, token);

    [Fact]
    public async Task Lists_leaf_users_via_nested_groups_honouring_exclusion()
    {
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tuple("group", "staff", "member", new SubjectRef("user", "alice")),
            Tuple("group", "staff", "member", new SubjectRef("user", "bob")),
            Tuple("doc", "D1", "blocked", new SubjectRef("user", "bob")));      // bob revoked

        var result = await auth.ListSubjectsAsync(Req());
        result.Subjects.Select(s => s.Id).ShouldBe(new[] { "alice" });
    }

    [Fact]
    public async Task Paginates_subjects_to_exact_page_size()
    {
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tuple("group", "staff", "member", new SubjectRef("user", "a")),
            Tuple("group", "staff", "member", new SubjectRef("user", "b")),
            Tuple("group", "staff", "member", new SubjectRef("user", "c")));

        var page1 = await auth.ListSubjectsAsync(Req(pageSize: 2));
        page1.Subjects.Select(s => s.Id).ShouldBe(new[] { "a", "b" });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListSubjectsAsync(Req(pageSize: 2, token: page1.ContinuationToken));
        page2.Subjects.Select(s => s.Id).ShouldBe(new[] { "c" });
        page2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_surfaces_as_star_and_respects_page_size()
    {
        // A public grant (viewer@user:*) surfaces as the "*" subject and flows through
        // the normal confirm + paginate loop — it must NOT push the page over PageSize.
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "*")),
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "a")),
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "b")));

        var page1 = await auth.ListSubjectsAsync(Req(pageSize: 2));
        page1.Subjects.Count.ShouldBe(2);                       // exactly PageSize, no bonus row
        page1.Subjects.Select(s => s.Id).ShouldBe(new[] { "*", "a" });   // "*" sorts first ordinal
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListSubjectsAsync(Req(pageSize: 2, token: page1.ContinuationToken));
        page2.Subjects.Select(s => s.Id).ShouldBe(new[] { "b" });
        page2.ContinuationToken.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter ListSubjectsTests`
Expected: FAIL — `ListSubjectsAsync` still throws `NotImplementedException`.

- [ ] **Step 3: Implement `ListSubjectsAsync` (remove the stub)**

Delete the `ListSubjectsAsync` stub from `EngineDrivenAuthorizer.List.cs`, and add:

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListSubjects.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public async Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        index.Permission(request.Object.Type, request.Permission);   // validate

        // Forward-collect candidate leaf users from the whole permission expansion.
        var candidateUsers = new SortedSet<string>(StringComparer.Ordinal);
        var sawWildcardUser = false;
        var visited = new HashSet<EvalFrame>();
        await CollectLeafUsersAsync(index, request.Tenant, request.Object, request.Permission,
            candidateUsers, visited, v => sawWildcardUser = true, ct);

        // A public grant surfaces as the wildcard subject "*": add it to the candidate set so it
        // flows through the same confirm + paginate loop (no special-casing, exact page sizes).
        // "*" (0x2A) sorts first ordinal; CheckPermissionAsync(user:*) returns the public-grant
        // truth and an exclusion on user:* still denies it correctly.
        if (sawWildcardUser) candidateUsers.Add("*");

        // Confirm each candidate with the pointwise Check (honours exclusion/intersection).
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
                index, request.Tenant, request.Object, request.Permission, subject, request.Context, ctx, explain: null, ct);
            if (!ok) continue;

            confirmed.Add(subject);
            lastConfirmed = id;
            if (confirmed.Count == request.PageSize)
            {
                exhausted = !await AnySubjectConfirmedAfterAsync(index, request, candidateUsers, id, ct);
                break;
            }
        }

        var token = exhausted ? null : ContinuationCursor.Encode(lastConfirmed!);
        return new ListSubjectsResult(confirmed, token);
    }

    private async Task<bool> AnySubjectConfirmedAfterAsync(
        SchemaIndex index, ListSubjectsRequest request, SortedSet<string> users, string afterId, CancellationToken ct)
    {
        foreach (var id in users)
        {
            if (string.CompareOrdinal(id, afterId) <= 0) continue;
            var ctx = new EvalContext(_options);
            var ok = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission,
                new SubjectRef("user", id), request.Context, ctx, explain: null, ct);
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// Walks a permission expression forward, collecting every concrete leaf <c>user</c>
    /// reachable through relations, nested group membership, and arrow targets. Records
    /// whether a <c>user:*</c> wildcard was seen. Cycle-guarded via <paramref name="visited"/>.
    /// </summary>
    private async Task CollectLeafUsersAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SortedSet<string> users, HashSet<EvalFrame> visited, Action<bool> onWildcardUser, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, new SubjectRef("user", "<collect>"));
        if (!visited.Add(frame)) return;

        var def = index.Permission(obj.Type, permission);
        await CollectFromExprAsync(index, tenant, obj, def.Expression, users, visited, onWildcardUser, ct);
    }

    private async Task CollectFromExprAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SortedSet<string> users, HashSet<EvalFrame> visited, Action<bool> onWildcardUser, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
                await CollectFromRelationAsync(index, tenant, obj, r.Relation, users, visited, onWildcardUser, ct);
                break;
            case Union u:
                await CollectFromExprAsync(index, tenant, obj, u.Left, users, visited, onWildcardUser, ct);
                await CollectFromExprAsync(index, tenant, obj, u.Right, users, visited, onWildcardUser, ct);
                break;
            case Intersect i:
                await CollectFromExprAsync(index, tenant, obj, i.Left, users, visited, onWildcardUser, ct);
                await CollectFromExprAsync(index, tenant, obj, i.Right, users, visited, onWildcardUser, ct);
                break;
            case Exclude e:
                // Collect candidates from both sides; the confirm-by-Check step applies the exclusion.
                await CollectFromExprAsync(index, tenant, obj, e.Left, users, visited, onWildcardUser, ct);
                await CollectFromExprAsync(index, tenant, obj, e.Right, users, visited, onWildcardUser, ct);
                break;
            case Conditioned c:
                await CollectFromExprAsync(index, tenant, obj, c.Inner, users, visited, onWildcardUser, ct);
                break;
            case Arrow a:
            {
                var edges = await _relations.GetByObjectAsync(tenant, obj, a.Relation, ct);
                foreach (var edge in edges)
                {
                    var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
                    if (index.TryPermission(related.Type, a.Permission, out _))
                        await CollectLeafUsersAsync(index, tenant, related, a.Permission, users, visited, onWildcardUser, ct);
                    else
                        await CollectFromRelationAsync(index, tenant, related, a.Permission, users, visited, onWildcardUser, ct);
                }
                break;
            }
        }
    }

    private async Task CollectFromRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SortedSet<string> users, HashSet<EvalFrame> visited, Action<bool> onWildcardUser, CancellationToken ct)
    {
        var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
        foreach (var tuple in tuples)
        {
            var s = tuple.Subject;
            if (s.IsWildcard && string.Equals(s.Type, "user", StringComparison.Ordinal))
                onWildcardUser(true);
            else if (!s.IsSubjectSet && string.Equals(s.Type, "user", StringComparison.Ordinal))
                users.Add(s.Id);
            else if (s.IsSubjectSet)
            {
                var nested = new EntityRef(s.Type, s.Id);
                await CollectFromRelationAsync(index, tenant, nested, s.Relation!, users, visited, onWildcardUser, ct);
            }
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter ListSubjectsTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.ListSubjects.cs src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.List.cs tests/Custodex.Core.Tests/Evaluation/ListSubjectsTests.cs
git commit -m "feat: implement engine-driven list subjects with leaf-user expansion"
```

---

### Task 5: `BatchCheckAsync` — shared per-request memo

**Files:**
- Create: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Batch.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/BatchCheckTests.cs`

**Interfaces:**
- Produces: `BatchCheckAsync` (replaces the `m0/05` stub) evaluating every `CheckItem` against **one shared `EvalContext`** (so repeated sub-checks across items are memoized once), returning a `CheckResult` per item in request order.
- Consumes: `CheckPermissionAsync`, `EvalContext`, `SchemaIndex`.

> **Why one memo.** A batch typically asks many `(object, perm)` for the same subject, or many subjects against overlapping nested groups. Sharing the memo across items collapses the redundant nested-group expansions. Explain is not produced in batch (the contract's `CheckResult` per item still allows it, but batch defaults to decision-only); items are independent allow/deny.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/BatchCheckTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class BatchCheckTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<EngineDrivenAuthorizer> NewAsync()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))
            .Build();
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await relations.WriteAsync(T, new[]
        {
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("doc", "D2"), "viewer", new SubjectRef("user", "bob")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "alice")),
        }, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    [Fact]
    public async Task Batch_returns_a_result_per_item_in_order()
    {
        var auth = await NewAsync();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"),
            new Dictionary<string, object?>());
        var req = new BatchCheckRequest(T, new[]
        {
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")),  // via group => true
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob")),    // false
            new CheckItem(new EntityRef("doc", "D2"), "view", new SubjectRef("user", "bob")),    // direct => true
        }, ctx);

        var results = await auth.BatchCheckAsync(req);
        results.Count.ShouldBe(3);
        results[0].Allowed.ShouldBeTrue();
        results[1].Allowed.ShouldBeFalse();
        results[2].Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Empty_batch_returns_empty_list()
    {
        var auth = await NewAsync();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"),
            new Dictionary<string, object?>());
        var results = await auth.BatchCheckAsync(new BatchCheckRequest(T, Array.Empty<CheckItem>(), ctx));
        results.ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter BatchCheckTests`
Expected: FAIL — `BatchCheckAsync` still throws `NotImplementedException`.

- [ ] **Step 3: Implement `BatchCheckAsync` (remove the stub)**

Delete the `BatchCheckAsync` stub from `EngineDrivenAuthorizer.List.cs` (which is now empty and may be deleted), and add:

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Batch.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        var ctx = new EvalContext(_options);   // one shared memo across all items
        var results = new List<CheckResult>(request.Items.Count);

        foreach (var item in request.Items)
        {
            var allowed = await CheckPermissionAsync(
                index, request.Tenant, item.Object, item.Permission, item.Subject,
                request.Context, ctx, explain: null, ct);
            results.Add(new CheckResult(allowed));
        }
        return results;
    }
}
```

> If `EngineDrivenAuthorizer.List.cs` now holds no members, delete the file and drop it from the commit. The three operations live in their own partial files.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter BatchCheckTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full evaluation suite**

Run: `dotnet test tests/Custodex.Core.Tests --filter Evaluation`
Expected: PASS (all Check + List + Batch tests).

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Batch.cs tests/Custodex.Core.Tests/Evaluation/BatchCheckTests.cs
git commit -m "feat: implement batch check with shared per-request memo"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] `ListObjects` is the oracle: candidate superset (reverse-reachable ∪ type universe) confirmed by the pointwise Check — exclusion/intersection honoured (Task 3).
- [ ] Pagination returns exactly `PageSize` confirmed ids except at the true end; cursor is opaque, deterministic, resumable; no dupes, no gaps across the full range (Task 3).
- [ ] Wildcard `type:*` grants surface every object of the type in `ListObjects` (Task 3).
- [ ] `ListSubjects` expands nested groups and arrow targets to leaf users, confirms each with Check, and surfaces an unexcluded `user:*` public grant (Task 4).
- [ ] `BatchCheck` shares one `EvalContext` memo across all items and returns results in request order (Task 5).
- [ ] All three operations validate the request type/permission against the schema (throw `Unknown*Exception` on bad input).

## Contract gaps (reported, not changed)

- **`IRelationStore` lacks a forward-by-type scan.** The engine-driven `ListObjects` oracle needs "all objects of type T" to honour wildcard grants, which the portable `IRelationStore` contract (`GetByObjectAsync`/`GetBySubjectAsync`/`WriteAsync`) does not provide. Resolved **without** editing `README.md` by adding a provider-specific `GetObjectsOfTypeAsync` to `InMemoryRelationStore` only (an oracle convenience), not to the `IRelationStore` interface. The Postgres provider (`m1/06`) computes the type universe inside its CTE instead, so no portable-contract change is needed; if a future portable need arises, add the method to `IRelationStore` in the contract first.
