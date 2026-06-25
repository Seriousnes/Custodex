# M0/08 — Caching Orchestration

**Goal:** A caching decorator over the authorizer that adds a cross-request `ICacheStore` check cache keyed by `(store, tenant, schema_version, object, permission, subject)`, caching **only unconditioned** results, with epoch-based invalidation and `CustodexDiagnostics.CacheHits`/`CacheMisses` accounting.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** `CachingAuthorizer` wraps the internal `ICacheableAuthorizer` seam (m0/05) — not the public `IAuthorizer` — because the cacheability signal (`ConditionTouched`) lives on `CheckInternalAsync`. On `CheckAsync` it reads the active schema version, reads the current tenant epoch via `ICacheStore.GetEpochAsync`, builds the cache key, and treats a missing entry or an epoch mismatch as a miss. On a miss it calls the inner `CheckInternalAsync`, caches the result **only when no condition was touched** (stamped with the current epoch), and records hits/misses. `Explain`, `ListObjects`, `ListSubjects`, and `BatchCheck` pass straight through uncached. Writes bump the epoch in the same transaction (the actual wiring is M1; this plan establishes the seam).

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly. Tests use `InMemoryCacheStore` (m0/04).

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/05–m0/07 (`ICacheableAuthorizer`/`CheckInternalAsync`, `EngineDrivenAuthorizer`), m0/01 (`ICacheStore`, `CacheEntry`, `CustodexDiagnostics`), m0/04 (`InMemoryCacheStore`) — see README.

**Caching rules (spec §9.1):**
- Cache key is `(store, tenant, schema_version, object, permission, subject)`. The schema version is read from the active schema, so a schema change naturally re-keys (old entries become unreachable).
- **Only unconditioned results are cached** — the `ConditionTouched` signal from `CheckInternalAsync` decides. Attribute/context-dependent results are recomputed every time.
- Invalidation is a coarse per-`(store, tenant)` epoch, bumped on any write and committed in the same transaction; the decorator compares `CacheEntry.Epoch` to `GetEpochAsync` and treats a mismatch as a miss.

---

### Task 1: Cache key builder

- [ ] **Files:** create `src/Custodex.Core/Caching/CheckCacheKey.cs`. Test: `tests/Custodex.Core.Tests/Caching/CheckCacheKeyTests.cs`.

**Produces:** `static string CheckCacheKey.Build(TenantContext tenant, string schemaVersion, EntityRef obj, string permission, SubjectRef subject)`.
**Consumes (see README):** `TenantContext`, `EntityRef`, `SubjectRef`.

**Behavior:** a stable, collision-resistant key over the six components. Identifiers are joined with the ASCII Unit Separator (a control char never valid in an identifier), prefixed with the field role, so `object=a, perm=bc` cannot collide with `object=ab, perm=c`. The subject's `Relation` is part of the key (a subject-set differs from a bare subject).

**Cases to pin:**

| Setup | Expect |
|---|---|
| identical inputs | identical key |
| each of the six components varied | a different key |
| subject with vs without a relation | different keys |

**Done when:** build clean under TreatWarningsAsErrors; stability and component-distinctness hold.

---

### Task 2: Boolean cache-value codec

- [ ] **Files:** create `src/Custodex.Core/Caching/CacheValueCodec.cs`. Test: `tests/Custodex.Core.Tests/Caching/CacheValueCodecTests.cs`.

**Produces:** `static byte[] CacheValueCodec.Encode(bool)` and `static bool CacheValueCodec.Decode(byte[])`.
**Consumes (see README):** nothing beyond the BCL.

**Behavior:** `CacheEntry.Value` is `byte[]`, so a `bool` decision is one byte (`1`/`0`).

**Cases to pin:**

| Setup | Expect |
|---|---|
| encode then decode true / false | round-trips |
| encode | single byte |

**Done when:** build clean; round-trip and single-byte hold.

---

### Task 3: `CachingAuthorizer` — read-through with epoch validation

- [ ] **Files:** create `src/Custodex.Core/Caching/CachingAuthorizer.cs`. Test: `tests/Custodex.Core.Tests/Caching/CachingAuthorizerTests.cs`.

**Produces:** `CachingAuthorizer : IAuthorizer` wrapping `ICacheableAuthorizer` + `ISchemaStore` (for the schema version) + `ICacheStore`, with an internal constructor `(ICacheableAuthorizer inner, ISchemaStore schemaStore, ICacheStore cache, TimeSpan? ttl = null)` (default TTL 5 minutes).
**Consumes (see README):** `ICacheableAuthorizer.CheckInternalAsync` (m0/05); `ICacheStore`, `CacheEntry`, `CustodexDiagnostics.CacheHits`/`CacheMisses`; `CheckCacheKey`, `CacheValueCodec`.

**Behavior:**
- `CheckAsync` with `Explain` bypasses the cache (a fresh trace). Otherwise read the active schema (throw if none), read the epoch, build the key, and look up the entry; on an entry whose epoch matches, count a hit and decode it. On a miss (absent entry or epoch mismatch) count a miss, call the inner `CheckInternalAsync`, and cache the result **only when `ConditionTouched` is false**, stamping the entry with the epoch read above. `BatchCheckAsync`/`ListObjectsAsync`/`ListSubjectsAsync` delegate to the inner authorizer uncached.

**Cases to pin:**

| Setup | Expect |
|---|---|
| repeat an identical unconditioned check | second served from cache (no further store reads) |
| bump the epoch between checks | next check misses and recomputes |
| a tuple-carried condition is touched | result never cached (recomputed each time) |
| `Explain: true` repeated | bypasses cache; fresh trace each time |
| one miss then one hit | `CacheMisses` and `CacheHits` both recorded |

**Done when:** build clean; cache-hit, epoch-invalidation, unconditioned-only, explain-bypass, and metrics all hold.

---

### Task 4: Write-path epoch-bump integration seam

- [ ] **Files:** create `src/Custodex.Core/Caching/CacheInvalidation.cs`. Test: `tests/Custodex.Core.Tests/Caching/CacheInvalidationTests.cs`.

**Produces:** `static Task CacheInvalidation.OnWriteAsync(ICacheStore cache, TenantContext tenant, IUnitOfWork uow, CancellationToken ct = default)` — the single call the M1 write path invokes inside its transaction to bump the epoch.
**Consumes (see README):** `ICacheStore.BumpEpochAsync`/`GetEpochAsync`.

**Behavior** (spec §9.2): the real bump happens in the write path, committed in the same transaction as the tuple/attribute write. M0 has no transactional write path, so this task establishes the seam and proves the invariant: after a bump, an entry stamped with the old epoch is no longer valid (a reader treats it as a miss). m1/07 wires `OnWriteAsync` into the write operations.

**Cases to pin:**

| Setup | Expect |
|---|---|
| set an entry, then `OnWriteAsync`, then re-read the epoch | epoch changed; the stored entry's epoch no longer matches (stale) |

**Done when:** build clean; the bump makes a prior entry stale.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] Cache key includes all six components, with the subject-set relation included.
- [ ] A repeated unconditioned check hits the cache (no further store reads).
- [ ] An epoch bump invalidates: the next check misses and recomputes.
- [ ] Conditioned results (any condition touched) are never cached.
- [ ] `Explain` requests bypass the cache and return a fresh trace.
- [ ] `CacheHits`/`CacheMisses` are recorded; `List`/`Batch` delegate uncached.
- [ ] The write-path epoch-bump seam (`CacheInvalidation.OnWriteAsync`) exists for m1/07 to wire in.
