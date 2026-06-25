# M1/08 — Differential Harness: CTE vs Oracle

**Goal:** A CsCheck differential, property-based harness that generates random **valid** schemas + tuples and asserts `NpgsqlCteAuthorizer` (the Postgres CTE path, M1/05/06) ≡ `EngineDrivenAuthorizer` (the engine-driven oracle, M0/05/07) for **Check, ListObjects, and ListSubjects**, over a Testcontainers Postgres database and an in-memory store seeded identically. This is the project's correctness backbone (spec §7.4 / §11.1): the CTE path is trusted only when the oracle agrees across thousands of cases.

**For implementers:** drive with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; TDD (Red → Green → Commit) per task; checkboxes track progress; one conventional-commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture/approach:** the harness lives **inside the existing `Custodex.Storage.Postgres.Tests` project** under namespace/folder `Differential/` (not a separate project; README post-dispatch reconciliation 7). It reuses the project's `PostgresFixture` and `[Collection("postgres")]`. One generator emits a `(Schema, tuples, attributes, probes)` model drawn from a fixed family of **curated valid skeletons** so every case passes M0/03 validation (a fully random schema is usually invalid — both authorizers would throw in lockstep and prove nothing). The harness seeds the same model into (a) the in-memory provider behind `EngineDrivenAuthorizer` and (b) a fresh `(store, "t")` tenant in Postgres behind `NpgsqlCteAuthorizer`, using the same `IConditionEvaluator`, then asserts: Check agrees per `(object, permission, subject)`; the **set** of ListObjects ids agrees per `(subject, type, permission)`; the **set** of ListSubjects subjects agrees per `(object, permission)`. Sets, not sequences — pagination order is internal. A CsCheck counterexample is a genuine CTE bug; the oracle is the spec.

**Tech stack:** .NET 10 (`net10.0`), C# 14, **CsCheck** (MIT), Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Uses `Custodex.Core` (oracle), `Custodex.Storage.InMemory`, `Custodex.Storage.Postgres`.

**Global Constraints:** see `../README.md` → Global Constraints. Identifiers are domain-neutral (the `tests/` tree is vocabulary-guarded), so the skeletons use abstract nouns (`doc`/`crate`/`asset`/`team`/`repo`).

**Dependencies:** builds on `m0/03` (`SchemaValidator` — the generated schema must pass it), `m0/04` (in-memory stores + `NoOpUnitOfWork`), `m0/05`/`m0/07` (the oracle), M1/01–M1/06 (Postgres schema, stores, `NpgsqlCteAuthorizer`).

> **Calibration — this plan IS the correctness mechanism.** The M1/02 spike proved the seam on hand-checked cases; this harness generalizes it to thousands of random ones. The generators and equivalence assertions are the durable spec. A divergence is a CTE bug — fix `NpgsqlCteAuthorizer`, never weaken the harness.

---

### Task 1: The dual-seed harness

- [ ] **Files:** create `…Tests/Differential/DifferentialHarness.cs`; smoke test `…Tests/Differential/HarnessSmokeTests.cs` (with a small `ModelGeneratorSamples` helper for the hand-picked discriminator model). Reuses the project's `PostgresFixture`.

**Produces:** `DifferentialHarness.BuildAsync(PostgresFixture, GeneratedModel, store, ct)` — seeds the model into the in-memory stores (behind the oracle, via `NoOpUnitOfWork`) and into a fresh `(store, "t")` tenant in Postgres (behind the CTE authorizer), using the same `NullConditionEvaluator` for both, then returns the two authorizers.
**Consumes (see README):** in-memory stores + `NoOpUnitOfWork` (M0/04), `EngineDrivenAuthorizer` (M0/05), the Npgsql stores + `NpgsqlUnitOfWorkFactory` (M1/03/04), `NpgsqlCteAuthorizer` (M1/05); `GeneratedModel` (Task 2).

**Behavior:** both authorizers receive identical schema + tuples + attributes. A unique `store` per case keeps Postgres state from leaking between iterations. The Postgres seed runs inside one `NpgsqlUnitOfWork` (insert store + tenant, set schema, write tuples + attributes, commit). A fixed Unix-epoch `RequestContext.Now` keeps conditions deterministic. The smoke test pins one hand-picked discriminator model (inner exclusion through an arrow) and asserts both authorizers deny the blocked subject.

**Cases to pin:**

| Setup | Expect |
|---|---|
| hand-picked arrow-inner-exclusion model | both authorizers deny the editor-and-blocked subject |

**Done when:** build clean; smoke case passes (Postgres required).

---

### Task 2: The valid-schema + tuples + probes generator

- [ ] **Files:** create `…Tests/Differential/ModelGenerator.cs` (+ the `GeneratedModel` record); test `…Tests/Differential/ModelGeneratorTests.cs`.

**Produces:** `ModelGenerator.Gen` — a CsCheck `Gen<GeneratedModel>` where `GeneratedModel(Schema, Tuples, Attributes, ProbeObjects, ProbeSubjects)`. The schema is drawn from a **fixed family of valid skeletons**; the tuple graph is randomized over a small id pool; probes are the entities/subjects the harness will Check/List against.
**Consumes (see README):** `SchemaBuilder` (M0/02), `SchemaValidator` (M0/03), CsCheck `Gen`.

**Behavior:** four curated, always-valid skeletons exercise the hard interactions; only the data graph varies (which ids fill which relations, group/subject-set nesting depth, who is blocked, whether the gate is flagged):
- **S1** — nested-group viewer with exclusion (`doc.view = viewer - blocked`; `viewer` accepts user, `group#member`, or `user:*`), with a two-hop nested group chain and three docs sharing one viewer group to exercise pagination.
- **S2** — arrow-with-inner-exclusion (`asset.edit = crate->edit`, `crate.edit = editor - blocked` — the discriminator shape).
- **S3** — intersection-through-arrow wildcard gate (spec §12.5 shape): access via a direct grant outside a flagged crate, or both group memberships when inside a flagged crate.
- **S4** — subject-set nesting under **non-group/member** identifiers (`team#owner`-style), with a two-hop nest, to exercise the *generic* subject-set climb the M1/06 candidate CTE relies on.

A validation property asserts every generated schema passes M0/03 — so both authorizers run real evaluations, never throw in lockstep.

**Cases to pin:**

| Setup | Expect |
|---|---|
| sample many models | every generated schema passes `SchemaValidator` |
| sample models | each has non-empty probe objects and subjects |

**Done when:** build clean; both properties pass (no Postgres).

---

### Task 3: Differential Check property

- [ ] **Files:** create `…Tests/Differential/CheckEquivalenceTests.cs`.

**Consumes (see README):** `ModelGenerator.Gen`, `DifferentialHarness`.

**Behavior:** `Check.SampleAsync(ModelGenerator.Gen, …)` runs an async predicate per model: a unique store per iteration, build both authorizers, and for each `(probeObject, permission, probeSubject)` assert `oracle.CheckAsync.Allowed == cte.CheckAsync.Allowed` (every permission on the probe object's type is enumerated from the generated schema). A divergence returns false; CsCheck minimizes the model. Keep `iter` modest (each iteration spins a fresh Postgres tenant) — a few hundred models over the skeletons is thousands of comparisons.

**Cases to pin:**

| Setup | Expect |
|---|---|
| random valid models, all probe triples | CTE Check ≡ oracle Check |

**Done when:** build clean; the property passes (Postgres required); a failure is a CTE bug to fix in M1/05.

---

### Task 4: Differential ListObjects and ListSubjects properties

- [ ] **Files:** create `…Tests/Differential/ListEquivalenceTests.cs`.

**Consumes (see README):** `ModelGenerator.Gen`, `DifferentialHarness`.

**Behavior:** two properties. (1) for each `(probeSubject, type, permission)`, the **set** of ListObjects ids agrees, drained across all pages (pageSize 2). (2) for each `(probeObject, permission)`, the **set** of ListSubjects subjects agrees, likewise drained. Both page each authorizer to exhaustion (token == null) and compare the unioned sets, so a candidate-generation miss (a true positive the SQL never surfaces) is caught.

**Cases to pin:**

| Setup | Expect |
|---|---|
| random valid models, drained pages | CTE ListObjects set ≡ oracle |
| random valid models, drained pages | CTE ListSubjects set ≡ oracle |

**Done when:** build clean; both properties pass (Postgres required); the whole `Differential` namespace is green; a divergence is a CTE list-path bug to fix in M1/06.

---

## Self-review checklist

- [ ] Build clean under TreatWarningsAsErrors; the harness lives in `Custodex.Storage.Postgres.Tests/Differential/` reusing `PostgresFixture`.
- [ ] Every generated schema passes M0/03 validation, so both authorizers run real evaluations.
- [ ] The four skeletons cover union, exclusion, arrow-into-permission, arrow-into-relation, nested groups, the inner-exclusion-through-arrow discriminator, the §12.5 wildcard gate, and a non-group subject-set climb (Task 2).
- [ ] The harness seeds the same model into the in-memory oracle and a fresh Postgres tenant with the same `IConditionEvaluator` (Task 1).
- [ ] Check equivalence holds over random models (Task 3).
- [ ] ListObjects and ListSubjects **set** equivalence holds, draining all pages (Task 4).
- [ ] Divergences are framed as CTE bugs to fix in M1/05/06; the oracle is never adjusted.
