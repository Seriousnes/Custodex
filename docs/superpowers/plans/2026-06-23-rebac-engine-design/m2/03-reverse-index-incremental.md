# M2/03 — Reverse-Index Incremental Maintenance

**Goal:** Maintain `reverse_index` incrementally, **inside the write transaction**, when tuples or attributes change: compute the **affected closure** (every object whose structural grants could have changed) and recompute each affected object's rows from scratch, so the index stays equal to the full rebuild (m2/02) — including the exclusion landmine (adding a `blocked` tuple removes rows; removing it re-adds them, even for objects reachable by multiple independent grant paths) and arrow-reachable changes. Rows are stamped with the active `schema_version`; a schema change yields a new version whose marker is absent, invalidating the index and owing a rebuild.

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD unit — Red → Green → one Conventional-Commit with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [ ]` checkboxes.

**Architecture/approach (recompute-the-affected-closure):** Surgical delta arithmetic on the reverse index is where exclusion and multi-path bugs hide — "this write adds these rows and removes those" is the spec's hardest landmine (§7.3). This plan **does not** do delta arithmetic. On a write it (1) computes the **affected closure** — the objects whose grants could change as a consequence of the changed tuples — then (2) for each affected object **recomputes that object's rows exactly as the rebuilder does** (probe every candidate subject × the object's permissions with the unconditioned structural Check) and (3) **replaces** that object's rows (delete-for-object then upsert). Recompute-per-object is correct-by-construction for exclusion and multi-path: it never reasons about deltas, it re-derives the full structural truth for each touched object — identical to the rebuild, scoped to the closure. The only failure mode is an **incomplete closure** (a changed object not recomputed), which the m2/06 differential harness exists to catch. The closure is computed by reverse traversal from the changed tuples and must be a **complete superset**: recomputing an unchanged object is harmless (it re-derives the same rows); missing one is the bug.

**Tech stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, Testcontainers.PostgreSql. Reuses `Custodex.Core` (the structural probe, `SchemaIndex`, `NullConditionEvaluator`) and m2/02 (`RebuildEnumeration`, the rebuild semantics this scopes).

**Global Constraints:** see `../README.md` → Global Constraints, and its Calibration note.

**Dependencies:** builds on m0/05 (the structural probe from m2/02 Task 1); m1/01–m1/04 (`reverse_index`, `MigrationRunner`, `NpgsqlUnitOfWork*`, the Npgsql stores); m1/07 (`AuditedWritePath` — the write orchestration this hooks into); m2/01 (`IIndexStore`/`NpgsqlIndexStore`, `ReverseIndexRow`); m2/02 (`ReverseIndexRebuilder`, `RebuildEnumeration`, the maintenance oracle).

> **Calibration (critical — the project's hardest landmine).** Incremental closure maintenance under exclusion is the single hardest correctness problem in the engine (spec §7.3). The **tests are the rigorous spec**: the two invariants — **index ≡ oracle** and **incremental ≡ full rebuild** — are pinned here on concrete cases and generalized by the m2/06 CsCheck differential harness across random write sequences. The maintenance code is **the approach validated by m2/06, NOT guaranteed-correct copy-paste**. The recompute-per-affected-object strategy is chosen precisely because it sidesteps delta arithmetic; its one failure mode is an incomplete closure, which m2/06 is built to find. The full rebuild (m2/02) is the always-correct safety net: if maintenance and rebuild ever disagree, maintenance is wrong and a rebuild repairs it.

---

### Task 1: Compute the affected closure from changed tuples

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Index/AffectedClosure.cs`; test `…Tests/Index/AffectedClosureTests.cs`.

**Produces:** `AffectedClosure.ComputeAsync(NpgsqlConnection, NpgsqlTransaction, TenantContext, IReadOnlyList<RelationTuple> changed, ct)` returning the distinct objects whose structural grants could change as a result of `changed` (the added ∪ removed tuples of a write).
**Consumes (see README):** `relation_tuples` (m1/01); `RelationTuple`/`EntityRef`/`TenantContext`.

**Behavior:** a reverse-index row for `(subject, perm, object)` depends on tuples reachable *downward* from `object` (its relations, the groups they name, the objects its arrows point at). So changing a tuple `X#rel@Y` can change the rows of any object that reaches `X` downward — i.e. `X` itself and everything **upward** of `X`. KEY DECISIONS:
- **Seeds** are each changed tuple's **object** (its rows depend on the changed tuple directly).
- **Direction** is an **inbound climb**: objects whose tuples name the seed object as their *subject*, transitively. This catches (a) structural arrows — `animal#enclosure@enclosure:KH1` means `animal` reaches `enclosure:KH1`, so a grant landing on the enclosure must recompute the animal; and (b) group-membership ripple — a changed `group#member` tuple's object (`group:G`) is a seed, and any `species#editor@group:G#member` names `group:G` as its subject, so the inbound climb from `group:G` reaches `species`.
- The recursive join matches `subject_type = otype AND subject_id = oid` **without constraining `subject_relation`**, so it climbs both structural edges and subject-set/group chains in one pass. This is the symmetric reverse of m1/06's forward candidate climb.
- **Must read on the write transaction** (the passed `tx`), so the closure reflects the post-write tuples committed-within-this-uow.
- Empty `changed` returns empty. The result is a **complete superset** — completeness is what m2/06 proves; if it ever finds a missed object, widen the climb.

Pseudocode (the recursive reachability):
```
reached := { (type,id) : each changed tuple's object }
repeat: reached += { (rt.object_type, rt.object_id)
                     : rt in relation_tuples (this tenant)
                       where rt.subject_type,rt.subject_id ∈ reached }
return distinct reached
```

**Cases to pin** (seed: `animal:EL-1`/`EL-2` arrow into `enclosure:KH1`; `species:kangaroo` editable via `group:macropods#member`; alice in macropods):

| Changed tuple | Affected closure contains |
|---|---|
| `blocked@alice` on `species:kangaroo` (direct grant change) | `species:kangaroo` |
| `editor@bob` on `enclosure:KH1` (structural-edge change) | `enclosure:KH1`, `animal:EL-1`, `animal:EL-2` (arrow into KH1) |
| `member@carol` on `group:macropods` (membership change) | `group:macropods`, `species:kangaroo` (grants to the group) |

**Done when:** build clean; cases pass; closure reads on the write transaction; requires Docker.

---

### Task 2: Recompute one object's rows (scoped rebuild)

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Index/ObjectRowRecomputer.cs`; test `…Tests/Index/ObjectRowRecomputerTests.cs`.

**Produces:** `ObjectRowRecomputer(connectionString, IRelationStore, IAttributeStore)` with `RecomputeAsync(SchemaIndex, ISchemaStore, TenantContext, EntityRef obj, IReadOnlyList<string> candidateUsers, ct)` returning the structural rows that should exist for one object.
**Consumes (see README):** the structural probe + `EngineDrivenAuthorizer` (m2/02 Task 1); `SchemaIndex`, `NullConditionEvaluator` (m0/05); `ReverseIndexRow` (m2/01).

**Behavior:** the rebuild's inner loop, scoped to one object. For each permission declared on `obj`'s type and each candidate subject (`user:{id}` ∪ `user:*`), probe the unconditioned structural Check; emit a `ReverseIndexRow` per granted probe carrying its `conditioned` flag. KEY DECISIONS:
- **Sharing this probe-and-keep logic with the rebuild is what makes "incremental ≡ rebuild" hold by construction** — recomputing every object in the closure with this method produces exactly the rows a full rebuild would for those objects.
- A type the schema no longer declares yields no rows (the type lookup is the staleness boundary).
- `connectionString` is held for symmetry/future provider-side reads; today everything is driven through the injected `IRelationStore`/`IAttributeStore`. (The transaction-visibility wiring those reads need is Task 3's load-bearing note.)

**Cases to pin:**

| Setup | Expect |
|---|---|
| kangaroo `editor@group:macropods#member`; `blocked@bob`; alice+bob in macropods; recompute `species:kangaroo` for users `[alice,bob]` | one row `user:alice` on kangaroo (bob blocked), `conditioned=false` |

**Done when:** build clean; case passes; the recomputer honours exclusion exactly as the rebuild; requires Docker.

---

### Task 3: `ReverseIndexMaintainer` — closure, recompute, replace, in-transaction

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Index/ReverseIndexMaintainer.cs`; small addition to `src/Custodex.Core/Evaluation/SchemaIndex.cs` (see contract gaps); test `…Tests/Index/ReverseIndexMaintainerTests.cs`.

**Produces:** `ReverseIndexMaintainer(connectionString, ISchemaStore, IRelationStore, IAttributeStore, IIndexStore)` with `MaintainAsync(TenantContext, IReadOnlyList<RelationTuple> changed, IUnitOfWork, ct)`.
**Consumes (see README):** `AffectedClosure` (Task 1), `ObjectRowRecomputer` (Task 2), `RebuildEnumeration` (m2/02), `IIndexStore` (m2/01), `SchemaIndex`.

**Behavior:** `MaintainAsync`:
1. loads the active schema;
2. **if the index is not built for the active `schema.Version`, skip incremental work and return** — the index is stale (schema changed or never built); a rebuild is owed, the write still commits, and m2/04's ListObjects falls back to the CTE oracle until a rebuild runs;
3. computes the affected closure on the write transaction;
4. loads the candidate users once via `RebuildEnumeration`;
5. for each affected object: delete-for-object then upsert the recomputed rows — a clean replace.

All index writes go on the caller's unit of work, so maintenance commits **atomically with the tuple write** (spec §7.3/§9.2). KEY DECISIONS:
- **Replace-per-affected-object owns the exclusion landmine without delta arithmetic.** Adding `blocked` makes the affected object recompute and drop the now-revoked subject's row; removing it recomputes and re-adds — and because recompute re-derives the **full** truth, a multi-path object still granted by another path keeps its row while one no longer granted loses it. The `conditioned` flag is recomputed per row.
- An affected object whose type the schema no longer declares cannot carry rows; just clear it (uses the non-throwing type lookup, see contract gaps).
- **Transaction-visibility (load-bearing).** `AffectedClosure` reads on the write transaction, so it sees the just-written tuples. But the recomputer's structural probe reads through `IRelationStore`, whose m1/04 implementation opens its **own** connection and will **not** see uncommitted tuples from the write transaction. Therefore the maintainer must construct the recomputer's `EngineDrivenAuthorizer` over a **relation/attribute store bound to the write uow's connection/transaction** (a uow-bound read path on the m1/04 stores — see contract gaps), or the recompute will miss the in-flight write. The m2/06 harness pins this by asserting the post-maintenance index equals the rebuild, which fails loudly if reads miss the in-flight write.

**Cases to pin** (schema with `edit = editor - blocked`; seed via rebuild first; then write+maintain on one transaction as the write path will):

| Setup → write+maintain | Expect |
|---|---|
| kangaroo `editor@alice` (built) → add `blocked@alice` | alice edits `[]` (row removed) |
| kangaroo `editor@alice` + `blocked@alice` (built) → remove `blocked@alice` | alice edits `[kangaroo]` (row re-added) |
| kangaroo granted to alice via **both** direct + group; alice in macropods → add `blocked@alice` | alice edits `[]` — one block revokes all union paths after the union |
| kangaroo `editor@group:macropods#member`; alice in macropods → add `member@carol` | carol edits `[kangaroo]` (membership ripple) |
| `animal.edit = enclosure->edit`; `animal:EL-1` arrows `enclosure:KH1` → add `editor@dana` on KH1 | dana edits `[EL-1]` (arrow ripple through structural edge) |
| schema active but **never rebuilt** → write+maintain | index still not built for v1; alice edits `[]` — maintenance skipped, ListObjects (m2/04) falls back until rebuild |

**Done when:** build clean; cases pass; recompute-and-replace per affected object (no delta arithmetic); the exclusion add/remove + multi-path + arrow + membership ripples hold; the unbuilt-version skip holds; writes on the caller's uow; recomputer reads see the in-flight write (uow-bound); requires Docker.

---

### Task 4: Hook maintenance into the write path; schema change invalidates

- [ ] **Files:** create `src/Custodex.Storage.Postgres/Index/IndexedWritePath.cs`; test `…Tests/Index/IndexedWritePathTests.cs`.

**Produces:** `IndexedWritePath(AuditedWritePath inner, ReverseIndexMaintainer, IIndexStore)` wrapping m1/07's `AuditedWritePath` so every write also maintains the index on the same unit of work. Members mirror the inner path: write-tuples, write-attributes, set-schema.
**Consumes (see README):** `AuditedWritePath` (m1/07), `ReverseIndexMaintainer` (Task 3), `IIndexStore` (m2/01).

**Behavior:** one transaction, four effects. m1/07's `AuditedWritePath` already sequences tuple-write + change_log + epoch-bump on one uow; `IndexedWritePath` adds index maintenance as the fourth, so spec §9.2 atomicity holds (create object, write its tuple, sync its attribute, maintain the index — succeed or fail as one unit). KEY DECISIONS:
- **Write-tuples:** call the inner path, then maintain over `[..add, ..remove]` on the same uow.
- **Set-schema invalidates rather than maintains:** call the inner path, then clear the index. A new `schema_version` makes the old rows unreachable (stamped with the old version, new version's marker absent) and clear removes them outright, so a stale index is never served (spec §7.3).
- **Write-attributes:** call the inner path, then maintain by passing a **synthetic changed-tuple whose object is the attribute's object** (with a synthetic relation name like `*attributes*` that can never match a real relation, so it only contributes the object as a closure seed). This makes `AffectedClosure` seed from that object and climb upward, recomputing every object whose conditioned grants reference these attributes so their `conditioned` flags stay current. (Conditioned rows are re-checked at query time regardless, but the affected object is recomputed to keep the structural shape correct.) m2/06 includes attribute-write sequences.

**Cases to pin:**

| Setup → action | Expect |
|---|---|
| built index → write-tuples `editor@alice` on kangaroo, commit | alice edits `[kangaroo]` (maintained atomically) |
| built index → write-tuples `editor@alice` on wallaby, **no commit** (dispose rolls back) | index unchanged (alice edits empty) — index write enlisted in the uow |
| built v1 with rows → set-schema v2, commit | v1 rows cleared; v2 not built (rebuild owed) |

**Done when:** build clean; cases pass; a rolled-back write leaves the index unchanged; a schema change clears the index and leaves the new version unbuilt; requires Docker.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under `TreatWarningsAsErrors=true`.
- [ ] Maintenance recomputes-and-replaces per affected object (no delta arithmetic); rows equal the scoped rebuild.
- [ ] The exclusion landmine is owned: add `blocked` removes the row, remove `blocked` re-adds it — including a multi-path object where one block revokes all union paths (Task 3).
- [ ] Group-membership and arrow-reachable changes ripple through the closure to the right objects (Task 3).
- [ ] Rows stamped with the active `schema_version`; a not-built version makes maintenance a no-op (rebuild owed) and ListObjects (m2/04) falls back (Task 3).
- [ ] A schema change clears the index and leaves the new version unbuilt (Task 4).
- [ ] All index writes enlist in the caller's `IUnitOfWork`: a rolled-back write leaves the index unchanged (Task 4).
- [ ] The recomputer's relation reads see the in-flight write (uow-bound store), or the post-maintenance index would miss it.
- [ ] The recompute strategy and the affected closure are framed as "validated by the m2/06 differential harness," not guaranteed-correct copy-paste.

## Contract gaps / additions (reported, not changed)

- **`SchemaIndex` non-throwing type lookup (small `Custodex.Core` addition).** A `bool`-returning type lookup mirroring the existing `TryPermission`/`TryRelation` on `SchemaIndex` (m0/05), added in Task 3 so the maintainer can clear rows for an object whose type a schema change dropped. A `Custodex.Core` addition, not an `Custodex.Abstractions` contract change.
- **Uow-bound relation/attribute reads for the recomputer (transaction visibility).** Incremental maintenance must read the just-written tuples. `AffectedClosure` already reads on the write transaction; `ObjectRowRecomputer` reads through `IRelationStore`, whose m1/04 implementation opens its own connection and cannot see uncommitted writes. The maintainer therefore needs a relation/attribute store **bound to the write uow's connection** when building the recomputer's authorizer — i.e. m1/04's `NpgsqlRelationStore`/`NpgsqlAttributeStore` should offer a uow-bound construction (read on the uow's connection/transaction), or m1/03 should expose the connection for a per-uow store. Reported for the maintainer to add to m1/04; m2/06 pins the requirement by asserting the post-write index equals the rebuild.
- **`index_build_markers`** is reused from m2/01 (already reported there as a non-§6.3 table).
