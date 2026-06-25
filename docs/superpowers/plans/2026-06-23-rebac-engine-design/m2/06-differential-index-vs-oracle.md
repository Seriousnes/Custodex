# M2/06 — Differential: Reverse Index ≡ Oracle

**Goal:** Prove the maintained reverse index returns the same answers as the engine-driven oracle — for fresh rebuilds *and* after arbitrary sequences of writes — so incremental maintenance can never silently drift.

**For implementers:** drive this with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each `### Task` is one TDD unit — Red → Green → one Conventional-Commit with the co-author trailer (see `../README.md` → Global Constraints). Tasks are tracked with `- [ ]` checkboxes.

**Architecture/approach:** three-way agreement. For random valid schemas + tuples + write sequences, assert `index-backed ListObjects ≡ full-rebuild index ≡ EngineDrivenAuthorizer oracle`. The oracle (m0/05, m0/07) is ground truth; the full rebuild (m2/02) is the always-correct index; incremental maintenance (m2/03) is the fast path under test. This harness is the correctness mechanism for m2/03 (see the README Calibration note): a shrunk CsCheck counterexample points at m2/03 (the code under test), not at the test.

**Tech stack:** .NET 10, xUnit, Shouldly, CsCheck, Dapper/Npgsql, Testcontainers.PostgreSql.

**Global Constraints:** see `../README.md` → Global Constraints, and its Calibration note.

**Dependencies:** builds on m0/05+m0/07 (`EngineDrivenAuthorizer` oracle); m2/01 (`IIndexStore`/`NpgsqlIndexStore`); m2/02 (`ReverseIndexRebuilder` full rebuild); m2/03 (`ReverseIndexMaintainer` incremental maintenance + `IndexedWritePath`); m2/04 (`IndexedAuthorizer`). Reuses the model generator and the differential harness scaffold from m1/08.

---

### Task 1: Reuse the valid-model generator; add a write-sequence generator

- [ ] **Files:** create `tests/Custodex.Storage.Postgres.Tests/Differential/IndexModelGenerators.cs`; test `…/Differential/GeneratorSanityTests.cs`.

**Produces:** `IndexModelGenerators.WriteSequence` — a CsCheck `Gen` of write-op lists (each op is an add-or-remove of a `RelationTuple`), biased to include `blocked`/exclusion tuples and arrow-relevant tuples so exclusion-closure paths are exercised. A write-op record (`Add` flag + `Tuple`) is the unit.
**Consumes (see README):** the m1/08 `ModelGenerator` (curated valid-schema skeletons + tuple generator) and m0/03 schema validation.

**Behavior:** generate over m1/08's curated tuples for the active skeleton; for each tuple emit an add op, and sometimes follow a `blocked` add with a later remove op so the re-add closure (the exclusion landmine reversal) is exercised. The bias toward `blocked` and arrow tuples is what makes the harness probe the hard paths.

**Cases to pin — generator property:**

- Sampling `WriteSequence` over ~200 iterations yields at least one sequence containing a `blocked` (exclusion) tuple.

**Done when:** build clean; the sanity property passes.

---

### Task 2: Fresh-rebuild ≡ oracle

- [ ] **Files:** create `…/Differential/IndexRebuildEquivalenceTests.cs`; extend the m1/08 differential harness with an index path (`…/Differential/DifferentialHarness.Index.cs`).

**Produces:** the property test plus the harness extension that builds an `IndexedAuthorizer` over a rebuilt index and exposes a rebuild entry point. The m1/08 `SeedAsync` partial is updated to also construct the `ReverseIndexRebuilder` and the `IndexedAuthorizer` (the one-line wiring change shown in the extension).
**Consumes (see README):** `IndexedAuthorizer` (m2/04) over a rebuilt index; `EngineDrivenAuthorizer` (oracle); `ReverseIndexRebuilder.RebuildAsync` (m2/02); the m1/08 dual-seed helper extended with an index path. Both authorizers are built over the same Testcontainers Postgres model.

**Behavior — the invariant being asserted:** for random valid models, after a full rebuild, the index-backed `ListObjects` equals the oracle `ListObjects` for every `(subject, type, permission)` probe (compared sorted). The rebuild runs on a unit of work and commits before probing.

**Cases to pin — property/invariant:**

- For ~50 generated models: `IndexedAuthorizer.ListObjectsAsync ≡ EngineDrivenAuthorizer.ListObjectsAsync` (sorted object-ids) across all list probes, after a full rebuild.

**Done when:** build clean; the property passes over the configured iteration count; requires Docker.

---

### Task 3: Incremental ≡ rebuild ≡ oracle after write sequences

- [ ] **Files:** create `…/Differential/IndexIncrementalEquivalenceTests.cs`; add the write-application and scratch-rebuild helpers to the harness (`…/Differential/DifferentialHarness.Incremental.cs`).

**Produces:** the property test that proves incremental maintenance under exclusion/arrow changes is correct — the real correctness mechanism for m2/03. The harness gains: apply-one-write (writes the tuple + runs `ReverseIndexMaintainer.MaintainAsync` in the same uow, exactly as `IndexedWritePath` does) and rebuild-a-scratch-index (an independent full rebuild of the final state into a **separate** `reverse_index` namespace/scope so the two indexes do not collide) plus a second `IndexedAuthorizer` over that scratch index. `SeedEmptyAsync` wires the maintainer, the scratch rebuilder, and the scratch indexed authorizer.
**Consumes (see README):** `ReverseIndexMaintainer.MaintainAsync` (m2/03) invoked inside each write's unit of work; the rebuild and oracle paths for comparison; `IndexModelGenerators.WriteSequence` (Task 1).

**Behavior — the invariant being asserted:** generate a schema skeleton + a write sequence; apply each write through the normal path (incremental maintenance runs in-transaction); then independently full-rebuild the same final state into a scratch index. For every `(subject, type, permission)` probe, the incrementally-maintained index, the scratch-rebuilt index, and the oracle all return the same sorted object-ids. KEY DECISION: all three are compared **against the oracle**, not merely against each other — so a bug shared by rebuild and incremental cannot hide. A failing CsCheck case prints the minimal shrunk write-sequence; fix m2/03, not this test.

**Cases to pin — property/invariant:**

- For ~50 generated (schema, write-sequence) pairs: after applying the sequence, `incremental ≡ oracle` AND `scratch-rebuild ≡ oracle` for every list probe.
- The write sequences exercise add/remove of `blocked` (exclusion) and arrow-reachable tuples (from Task 1's bias).

**Done when:** build clean; the property passes; a shrunk counterexample points at m2/03 (the code under test); requires Docker.

---

## Self-review checklist (after all tasks)

- [ ] Both rebuild and incremental paths are compared against the oracle, not just against each other.
- [ ] Write sequences exercise add/remove of `blocked` (exclusion) and arrow-reachable tuples.
- [ ] A shrunk counterexample points at m2/03 (incremental maintenance), the code under test — this harness is the proof obligation for that plan.
