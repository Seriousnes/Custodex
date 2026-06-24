# M0/08 — Caching Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A caching decorator over `IAuthorizer` that adds a cross-request `ICacheStore` check cache keyed by `(store, tenant, schema_version, object, permission, subject)`, caching **only unconditioned** results, with epoch-based invalidation and `CustodexDiagnostics.CacheHits`/`CacheMisses` accounting.

**Architecture:** `CachingAuthorizer` wraps an inner `IAuthorizer` (the `EngineDrivenAuthorizer` from `m0/05`–`m0/07`). On `CheckAsync` it builds the cache key, reads the current tenant epoch via `ICacheStore.GetEpochAsync`, looks up a `CacheEntry`, and treats a missing entry or an epoch mismatch as a miss. On a miss it calls the inner authorizer through the internal `(Allowed, ConditionTouched)` path (from `m0/05`); it caches the result **only when no condition was touched**, stamping the entry with the current epoch. Writes bump the epoch (the actual bump happens in the write path / M1; this plan documents the integration point and records `CacheHits`/`CacheMisses`). `Explain`, `ListObjects`, `ListSubjects`, and `BatchCheck` pass straight through to the inner authorizer uncached.

**Tech Stack:** .NET 10, C# 14, xUnit, Shouldly. Uses `Custodex.Storage.InMemory`'s `InMemoryCacheStore` (from `m0/04`).

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings. Depends on `m0/05`–`m0/07` (`EngineDrivenAuthorizer` incl. internal `CheckInternalAsync`), `m0/01` (`ICacheStore`, `CacheEntry`, `CustodexDiagnostics`), `m0/04` (`InMemoryCacheStore`).

**Caching rules (spec §9.1):**
- Cache key: `(store, tenant, schema_version, object, permission, subject)`. Schema version is read from the active schema so a schema change naturally re-keys (old entries are unreachable).
- **Only unconditioned results are cached.** Attribute- and context-dependent results are recomputed every time. The "did this decision touch a condition" signal comes from `EngineDrivenAuthorizer.CheckInternalAsync` (returning `(bool Allowed, bool ConditionTouched)`), exposed in `m0/05`.
- Invalidation: a coarse per-`(store, tenant)` epoch, bumped on any write and committed in the same transaction. The decorator compares `CacheEntry.Epoch` to `ICacheStore.GetEpochAsync`; a mismatch is a miss.

---

### Task 1: Cache key builder

**Files:**
- Create: `src/Custodex.Core/Caching/CheckCacheKey.cs`
- Test: `tests/Custodex.Core.Tests/Caching/CheckCacheKeyTests.cs`

**Interfaces:**
- Produces: `static string CheckCacheKey.Build(TenantContext tenant, string schemaVersion, EntityRef obj, string permission, SubjectRef subject)` — a stable, collision-resistant string key over the six components.
- Consumes: `TenantContext`, `EntityRef`, `SubjectRef` from `Custodex.Abstractions`.

> The key must be unambiguous across components (so `object=a, perm=bc` cannot collide with `object=ab, perm=c`). Use a fixed separator that cannot appear in an identifier together with length-safe joining; here identifiers are joined with the ASCII Unit Separator (a control char never valid in an identifier), prefixed with the field role.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Caching/CheckCacheKeyTests.cs
using Custodex.Abstractions;
using Custodex.Core.Caching;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Caching;

public class CheckCacheKeyTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    [Fact]
    public void Same_inputs_produce_the_same_key()
    {
        var a = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"));
        var b = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"));
        a.ShouldBe(b);
    }

    [Fact]
    public void Different_components_produce_different_keys()
    {
        var baseKey = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"));
        CheckCacheKey.Build(T, "v2", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(new TenantContext("zoo", "t2"), "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D2"), "view", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "edit", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob")).ShouldNotBe(baseKey);
    }

    [Fact]
    public void Subject_set_relation_is_part_of_the_key()
    {
        var plain = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("group", "vets"));
        var set = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("group", "vets", "member"));
        plain.ShouldNotBe(set);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter CheckCacheKeyTests`
Expected: FAIL — `CheckCacheKey` not defined.

- [ ] **Step 3: Implement the key builder**

```csharp
// src/Custodex.Core/Caching/CheckCacheKey.cs
using Custodex.Abstractions;

namespace Custodex.Core.Caching;

public static class CheckCacheKey
{
    private const char Sep = '\u001F';   // ASCII Unit Separator: never valid inside an identifier

    public static string Build(
        TenantContext tenant, string schemaVersion, EntityRef obj, string permission, SubjectRef subject)
    {
        var subj = subject.Relation is null
            ? $"{subject.Type}:{subject.Id}"
            : $"{subject.Type}:{subject.Id}#{subject.Relation}";
        return string.Join(Sep,
            "Custodex.check",
            tenant.Store,
            tenant.Tenant,
            schemaVersion,
            $"{obj.Type}:{obj.Id}",
            permission,
            subj);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter CheckCacheKeyTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Caching/CheckCacheKey.cs tests/Custodex.Core.Tests/Caching/CheckCacheKeyTests.cs
git commit -m "feat: add check cache key builder"
```

---

### Task 2: Boolean cache-value codec

**Files:**
- Create: `src/Custodex.Core/Caching/CacheValueCodec.cs`
- Test: `tests/Custodex.Core.Tests/Caching/CacheValueCodecTests.cs`

**Interfaces:**
- Produces: `static byte[] CacheValueCodec.Encode(bool allowed)` and `static bool CacheValueCodec.Decode(byte[] value)`. `CacheEntry.Value` is `byte[]`, so a `bool` decision is one byte.
- Consumes: nothing beyond BCL.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Caching/CacheValueCodecTests.cs
using Custodex.Core.Caching;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Caching;

public class CacheValueCodecTests
{
    [Fact]
    public void Round_trips_true_and_false()
    {
        CacheValueCodec.Decode(CacheValueCodec.Encode(true)).ShouldBeTrue();
        CacheValueCodec.Decode(CacheValueCodec.Encode(false)).ShouldBeFalse();
    }

    [Fact]
    public void Encodes_to_a_single_byte()
    {
        CacheValueCodec.Encode(true).Length.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter CacheValueCodecTests`
Expected: FAIL — `CacheValueCodec` not defined.

- [ ] **Step 3: Implement the codec**

```csharp
// src/Custodex.Core/Caching/CacheValueCodec.cs
namespace Custodex.Core.Caching;

public static class CacheValueCodec
{
    public static byte[] Encode(bool allowed) => [allowed ? (byte)1 : (byte)0];
    public static bool Decode(byte[] value) => value.Length > 0 && value[0] == 1;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter CacheValueCodecTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Caching/CacheValueCodec.cs tests/Custodex.Core.Tests/Caching/CacheValueCodecTests.cs
git commit -m "feat: add boolean cache-value codec"
```

---

### Task 3: `CachingAuthorizer` — read-through with epoch validation

**Files:**
- Create: `src/Custodex.Core/Caching/CachingAuthorizer.cs`
- Test: `tests/Custodex.Core.Tests/Caching/CachingAuthorizerTests.cs`

**Interfaces:**
- Produces: `CachingAuthorizer : IAuthorizer` wrapping `EngineDrivenAuthorizer` + `ISchemaStore` (for the schema version) + `ICacheStore`, with constructor `CachingAuthorizer(EngineDrivenAuthorizer inner, ISchemaStore schemaStore, ICacheStore cache, TimeSpan? ttl = null)`. `CheckAsync` is read-through-cached for unconditioned results; `BatchCheckAsync`/`ListObjectsAsync`/`ListSubjectsAsync` delegate uncached; `CheckAsync` with `Explain=true` bypasses the cache (returns a fresh trace).
- Consumes: `EngineDrivenAuthorizer.CheckInternalAsync` (internal, from `m0/05`); `ICacheStore`, `CacheEntry`, `CustodexDiagnostics.CacheHits`/`CacheMisses`; `CheckCacheKey`, `CacheValueCodec`.

> **Why wrap `EngineDrivenAuthorizer` concretely (not `IAuthorizer`).** The cacheability signal (`ConditionTouched`) is only on the internal `CheckInternalAsync`, not the public `IAuthorizer`. The decorator therefore takes the concrete inner type. This is the resolution recorded as a Contract gap in `m0/05`. `InternalsVisibleTo("Custodex.Core.Tests")` (added in `m0/07`) lets the test see the internal call indirectly through `CachingAuthorizer`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Caching/CachingAuthorizerTests.cs
using System.Diagnostics.Metrics;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Caching;

public class CachingAuthorizerTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private sealed class CountingRelationStore : IRelationStore
    {
        private readonly InMemoryRelationStore _inner;
        public int GetByObjectCalls { get; private set; }
        public CountingRelationStore(InMemoryRelationStore inner) => _inner = inner;

        public Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
        { GetByObjectCalls++; return _inner.GetByObjectAsync(t, obj, relation, ct); }
        public Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(TenantContext t, SubjectRef subject, CancellationToken ct = default)
            => _inner.GetBySubjectAsync(t, subject, ct);
        public Task WriteAsync(TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct = default)
            => _inner.WriteAsync(t, add, remove, uow, ct);
    }

    private static Schema UnconditionedSchema() => new SchemaBuilder("v1")
        .Type("doc", t => t
            .Relation("viewer", s => s.User())
            .Permission("view", p => p.Relation("viewer")))
        .Build();

    private static Schema ConditionedSchema() => new SchemaBuilder("v1")
        .Type("doc", t => t
            .Relation("viewer", s => s.User())
            .Permission("view", p => p.Relation("viewer")))
        .Condition("always", c => { })
        .Build();

    private static async Task<(CachingAuthorizer Auth, CountingRelationStore Counter, InMemoryCacheStore Cache, IUnitOfWork Uow)>
        NewAsync(Schema schema, IConditionEvaluator conditions, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var raw = new InMemoryRelationStore();
        var counter = new CountingRelationStore(raw);
        var attributes = new InMemoryAttributeStore();
        var cache = new InMemoryCacheStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await raw.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        var inner = new EngineDrivenAuthorizer(schemaStore, counter, attributes, conditions);
        return (new CachingAuthorizer(inner, schemaStore, cache), counter, cache, new InMemoryUnitOfWork());
    }

    private static CheckRequest Req(string user, bool explain = false) => new(
        T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", user),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()), Explain: explain);

    [Fact]
    public async Task Second_identical_check_is_served_from_cache()
    {
        var (auth, counter, _, _) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));

        (await auth.CheckAsync(Req("alice"))).Allowed.ShouldBeTrue();
        var afterFirst = counter.GetByObjectCalls;
        afterFirst.ShouldBeGreaterThan(0);

        (await auth.CheckAsync(Req("alice"))).Allowed.ShouldBeTrue();
        counter.GetByObjectCalls.ShouldBe(afterFirst);   // no further store reads: cache hit
    }

    [Fact]
    public async Task Epoch_bump_invalidates_the_cache()
    {
        var (auth, counter, cache, uow) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));

        await auth.CheckAsync(Req("alice"));
        var afterFirst = counter.GetByObjectCalls;

        await cache.BumpEpochAsync(T, uow);   // simulate a write bumping the epoch
        await uow.CommitAsync();

        await auth.CheckAsync(Req("alice"));
        counter.GetByObjectCalls.ShouldBeGreaterThan(afterFirst);   // epoch mismatch => miss => recompute
    }

    [Fact]
    public async Task Conditioned_results_are_never_cached()
    {
        // A schema whose tuple carries a condition => result touches a condition => not cacheable.
        var schema = ConditionedSchema();
        var conditioned = new RelationTuple(
            new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice"),
            new ConditionRef("always", new Dictionary<string, object?>()));
        var (auth, counter, _, _) = await NewAsync(schema, new AlwaysTrueConditionEvaluator(), conditioned);

        await auth.CheckAsync(Req("alice"));
        var afterFirst = counter.GetByObjectCalls;

        await auth.CheckAsync(Req("alice"));
        counter.GetByObjectCalls.ShouldBeGreaterThan(afterFirst);   // recomputed: condition touched => not cached
    }

    [Fact]
    public async Task Explain_requests_bypass_the_cache()
    {
        var (auth, counter, _, _) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));

        var explained = await auth.CheckAsync(Req("alice", explain: true));
        explained.Explain.ShouldNotBeNull();
        var afterFirst = counter.GetByObjectCalls;

        var explainedAgain = await auth.CheckAsync(Req("alice", explain: true));
        explainedAgain.Explain.ShouldNotBeNull();
        counter.GetByObjectCalls.ShouldBeGreaterThan(afterFirst);   // never cached: fresh trace each time
    }

    [Fact]
    public async Task Records_cache_hits_and_misses()
    {
        long hits = 0, misses = 0;
        using var ml = new MeterListener();
        ml.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Custodex" && inst.Name is "Custodex.cache.hits" or "Custodex.cache.misses")
                l.EnableMeasurementEvents(inst);
        };
        ml.SetMeasurementEventCallback<long>((inst, m, _, _) =>
        {
            if (inst.Name == "Custodex.cache.hits") hits += m;
            else if (inst.Name == "Custodex.cache.misses") misses += m;
        });
        ml.Start();

        var (auth, _, _, _) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));

        await auth.CheckAsync(Req("alice"));   // miss
        await auth.CheckAsync(Req("alice"));   // hit
        ml.Dispose();

        misses.ShouldBeGreaterThanOrEqualTo(1);
        hits.ShouldBeGreaterThanOrEqualTo(1);
    }

    private sealed class AlwaysTrueConditionEvaluator : IConditionEvaluator
    {
        public bool Evaluate(ConditionDef definition, ConditionRef invocation,
            IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext context) => true;
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter CachingAuthorizerTests`
Expected: FAIL — `CachingAuthorizer` not defined.

- [ ] **Step 3: Implement `CachingAuthorizer`**

```csharp
// src/Custodex.Core/Caching/CachingAuthorizer.cs
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Core.Caching;

/// <summary>
/// Caching decorator over <see cref="EngineDrivenAuthorizer"/>. Caches only
/// unconditioned Check results, keyed by (store, tenant, schema_version, object,
/// permission, subject) and validated against the per-tenant epoch. List/Batch/
/// Explain delegate uncached.
/// </summary>
public sealed class CachingAuthorizer : IAuthorizer
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly EngineDrivenAuthorizer _inner;
    private readonly ISchemaStore _schemaStore;
    private readonly ICacheStore _cache;
    private readonly TimeSpan _ttl;

    public CachingAuthorizer(EngineDrivenAuthorizer inner, ISchemaStore schemaStore, ICacheStore cache, TimeSpan? ttl = null)
    {
        _inner = inner;
        _schemaStore = schemaStore;
        _cache = cache;
        _ttl = ttl ?? DefaultTtl;
    }

    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        // Explain bypasses the cache so the trace is always complete and fresh.
        if (request.Explain)
            return await _inner.CheckAsync(request, ct);

        var schema = await _schemaStore.GetActiveAsync(request.Tenant.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{request.Tenant.Store}'>");
        var epoch = await _cache.GetEpochAsync(request.Tenant, ct);
        var key = CheckCacheKey.Build(request.Tenant, schema.Version, request.Object, request.Permission, request.Subject);

        var entry = await _cache.GetAsync(key, ct);
        if (entry is not null && entry.Epoch == epoch)
        {
            CustodexDiagnostics.CacheHits.Add(1);
            return new CheckResult(CacheValueCodec.Decode(entry.Value));
        }

        CustodexDiagnostics.CacheMisses.Add(1);
        var (allowed, conditionTouched) = await _inner.CheckInternalAsync(request, ct);

        // Cache only unconditioned results; stamp with the epoch read above.
        if (!conditionTouched)
            await _cache.SetAsync(key, new CacheEntry(CacheValueCodec.Encode(allowed), epoch), _ttl, ct);

        return new CheckResult(allowed);
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => _inner.BatchCheckAsync(request, ct);

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => _inner.ListObjectsAsync(request, ct);

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => _inner.ListSubjectsAsync(request, ct);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter CachingAuthorizerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Caching/CachingAuthorizer.cs tests/Custodex.Core.Tests/Caching/CachingAuthorizerTests.cs
git commit -m "feat: add caching authorizer with epoch invalidation and unconditioned-only caching"
```

---

### Task 4: Write-path epoch-bump integration point (documentation + guard test)

**Files:**
- Create: `src/Custodex.Core/Caching/CacheInvalidation.cs`
- Test: `tests/Custodex.Core.Tests/Caching/CacheInvalidationTests.cs`

**Interfaces:**
- Produces: `static Task CacheInvalidation.OnWriteAsync(ICacheStore cache, TenantContext tenant, IUnitOfWork uow, CancellationToken ct)` — the single call the write path (`IRelationManager.WriteTuplesAsync`/`DeleteTuplesAsync`/`WriteAttributesAsync`, implemented in M1) invokes inside the write transaction to bump the epoch. M0 has no write path yet; this is the integration seam plus a guard test proving a bump makes a prior cached entry stale.
- Consumes: `ICacheStore.BumpEpochAsync`/`GetEpochAsync`.

> **Integration note.** The actual epoch bump happens in the write path, committed in the same transaction as the tuple/attribute write (spec §9.2). In M0 there is no transactional write path, so this task only establishes the seam (`CacheInvalidation.OnWriteAsync`) and proves the invariant: after a bump, an entry stamped with the old epoch is no longer valid. `m1/07` wires `OnWriteAsync` into `WriteTuplesAsync`/`DeleteTuplesAsync`/`WriteAttributesAsync`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/Caching/CacheInvalidationTests.cs
using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Caching;

public class CacheInvalidationTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    [Fact]
    public async Task OnWrite_bumps_the_epoch_so_old_entries_are_stale()
    {
        var cache = new InMemoryCacheStore();
        var uow = new InMemoryUnitOfWork();

        var before = await cache.GetEpochAsync(T);
        await cache.SetAsync("k", new CacheEntry([1], before), TimeSpan.FromMinutes(5));

        await CacheInvalidation.OnWriteAsync(cache, T, uow);
        await uow.CommitAsync();

        var after = await cache.GetEpochAsync(T);
        after.ShouldNotBe(before);

        var entry = await cache.GetAsync("k");
        // The entry still exists physically, but its epoch no longer matches => a reader treats it as a miss.
        entry.ShouldNotBeNull();
        (entry!.Epoch == after).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter CacheInvalidationTests`
Expected: FAIL — `CacheInvalidation` not defined.

- [ ] **Step 3: Implement the invalidation seam**

```csharp
// src/Custodex.Core/Caching/CacheInvalidation.cs
using Custodex.Abstractions;

namespace Custodex.Core.Caching;

/// <summary>
/// The cache-invalidation integration point for the write path. The M1 write path
/// calls <see cref="OnWriteAsync"/> inside the same transaction as a tuple/attribute
/// write, so the epoch bump commits atomically with the data change (spec §9.2).
/// </summary>
public static class CacheInvalidation
{
    public static Task OnWriteAsync(ICacheStore cache, TenantContext tenant, IUnitOfWork uow, CancellationToken ct = default)
        => cache.BumpEpochAsync(tenant, uow, ct);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter CacheInvalidationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Caching/CacheInvalidation.cs tests/Custodex.Core.Tests/Caching/CacheInvalidationTests.cs
git commit -m "feat: add write-path cache-invalidation seam"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Cache key includes all six components (store, tenant, schema_version, object, permission, subject), subject-set relation included (Task 1).
- [ ] A repeated unconditioned check hits the cache (no further store reads) (Task 3).
- [ ] An epoch bump invalidates: the next check is a miss and recomputes (Task 3).
- [ ] Conditioned results (any condition touched) are never cached (Task 3).
- [ ] `Explain` requests bypass the cache and return a fresh trace (Task 3).
- [ ] `CacheHits`/`CacheMisses` are recorded (Task 3).
- [ ] `List`/`Batch` delegate to the inner authorizer uncached (Task 3).
- [ ] The write-path epoch-bump seam (`CacheInvalidation.OnWriteAsync`) exists for `m1/07` to wire in (Task 4).

## Contract gaps (reported, not changed)

- **Cacheability signal is not on the public contract.** `m0/08` relies on `EngineDrivenAuthorizer.CheckInternalAsync` returning `(bool Allowed, bool ConditionTouched)` (introduced in `m0/05`) because the public `CheckResult` cannot express condition-dependence. The decorator therefore wraps the concrete `EngineDrivenAuthorizer`, not the `IAuthorizer` interface. This is the same gap reported by `m0/05`; recorded here for completeness. No `README.md` change made.
