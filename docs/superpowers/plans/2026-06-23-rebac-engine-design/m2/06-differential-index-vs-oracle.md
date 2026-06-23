# M2/06 — Differential: Reverse Index ≡ Oracle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the maintained reverse index returns the same answers as the engine-driven oracle — for fresh rebuilds *and* after arbitrary sequences of writes (so incremental maintenance can never silently drift).

**Architecture:** Three-way agreement. For random valid schemas + tuples + write sequences, assert `index-backed ListObjects ≡ full-rebuild index ≡ EngineDrivenAuthorizer oracle`. The oracle (m0/05, m0/07) is ground truth; the full rebuild (m2/02) is the always-correct index; incremental maintenance (m2/03) is the fast path under test.

**Tech Stack:** .NET 10, xUnit, Shouldly, CsCheck, Dapper/Npgsql, Testcontainers.PostgreSql.

## Global Constraints

See `../README.md` → Global Constraints and the Calibration section. Depends on: `m0/05`+`m0/07` (`EngineDrivenAuthorizer` oracle), `m2/01` (`IIndexStore`), `m2/02` (full rebuild), `m2/03` (incremental maintenance), `m2/04` (index-backed `ListObjects`). Reuses the model generator from `m1/08`.

---

### Task 1: Reuse the valid-model generator and add a write-sequence generator

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Differential/IndexModelGenerators.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Differential/GeneratorSanityTests.cs`

**Interfaces:**
- Consumes: `ModelGenerator` from `m1/08` (curated valid-schema skeletons + tuple generator) and `SchemaValidator` (m0/03).
- Produces: `IndexModelGenerators.WriteSequence` — a CsCheck `Gen<IReadOnlyList<WriteOp>>` where `WriteOp` is `record WriteOp(bool Add, RelationTuple Tuple)`, biased to include `blocked`/exclusion tuples and arrow-relevant tuples so exclusion-closure paths are exercised.

- [ ] **Step 1: Write the failing sanity test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Differential/GeneratorSanityTests.cs
using CsCheck;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Differential;

public class GeneratorSanityTests
{
    [Fact]
    public void Write_sequences_include_exclusion_tuples()
    {
        var sawBlocked = false;
        IndexModelGenerators.WriteSequence.Sample(ops =>
        {
            if (ops.Any(o => o.Tuple.Relation == "blocked")) sawBlocked = true;
        }, iter: 200);
        sawBlocked.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter GeneratorSanityTests`
Expected: FAIL — `IndexModelGenerators` not defined.

- [ ] **Step 3: Implement the generators**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Differential/IndexModelGenerators.cs
using CsCheck;
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Tests.Differential;

public sealed record WriteOp(bool Add, RelationTuple Tuple);

public static class IndexModelGenerators
{
    // Reuse m1/08's curated tuple generator for the active schema skeleton.
    public static readonly Gen<IReadOnlyList<WriteOp>> WriteSequence =
        Gen.Select(ModelGenerator.Tuples, Gen.Bool, (tuples, _) =>
        {
            var ops = new List<WriteOp>();
            foreach (var t in tuples)
            {
                ops.Add(new WriteOp(Add: true, t));
                // Sometimes add then later remove a blocked tuple to exercise re-add closure.
                if (t.Relation == "blocked") ops.Add(new WriteOp(Add: false, t));
            }
            return (IReadOnlyList<WriteOp>)ops;
        });
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter GeneratorSanityTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Differential
git commit -m "test: add reverse-index differential model generators"
```

---

### Task 2: Fresh-rebuild ≡ oracle

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Differential/IndexRebuildEquivalenceTests.cs`

**Interfaces:**
- Consumes: `IndexBackedAuthorizer` (m2/04) over a rebuilt index; `EngineDrivenAuthorizer` (oracle); `ReverseIndexRebuilder` (m2/02). Both authorizers built over the same Testcontainers Postgres model via the m1/08 dual-seed helper, extended with an index path.

- [ ] **Step 1: Write the failing property test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Differential/IndexRebuildEquivalenceTests.cs
using CsCheck;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Differential;

[Collection("postgres")]   // shares the Testcontainers fixture
public class IndexRebuildEquivalenceTests(PostgresFixture fx)
{
    [Fact]
    public async Task Index_listobjects_equals_oracle_after_full_rebuild()
    {
        await ModelGenerator.Model.SampleAsync(async model =>
        {
            await using var h = await DifferentialHarness.SeedAsync(fx, model);   // from m1/08, extended
            await h.RebuildIndexAsync();                                          // m2/02

            foreach (var (subject, type, permission) in h.ListProbes())
            {
                var oracle = await h.Oracle.ListObjectsAsync(h.ListReq(subject, type, permission));
                var index  = await h.IndexBacked.ListObjectsAsync(h.ListReq(subject, type, permission));
                index.ObjectIds.OrderBy(x => x).ShouldBe(oracle.ObjectIds.OrderBy(x => x));
            }
        }, iter: 50);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexRebuildEquivalenceTests`
Expected: FAIL — `DifferentialHarness.RebuildIndexAsync`/`IndexBacked` not present until the harness is extended.

- [ ] **Step 3: Extend the m1/08 harness with an index path**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Differential/DifferentialHarness.Index.cs
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Tests.Differential;

public partial class DifferentialHarness
{
    public IndexBackedAuthorizer IndexBacked { get; private set; } = default!;
    private ReverseIndexRebuilder _rebuilder = default!;

    public async Task RebuildIndexAsync()
    {
        await using var uow = await UowFactory.BeginAsync();
        await _rebuilder.RebuildAsync(Tenant, ActiveSchema.Version, uow);
        await uow.CommitAsync();
    }
}
```

> The `SeedAsync` partial (in m1/08) is updated to also construct `_rebuilder` and `IndexBacked`; show that one-line wiring change as part of this step.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexRebuildEquivalenceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Differential
git commit -m "test: assert rebuilt reverse index equals oracle"
```

---

### Task 3: Incremental ≡ rebuild ≡ oracle after write sequences

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Differential/IndexIncrementalEquivalenceTests.cs`

**Interfaces:**
- Consumes: `IncrementalIndexMaintainer` (m2/03) invoked inside each write's unit of work; the rebuild and oracle paths for comparison.

This is the test that proves incremental maintenance under exclusion/arrow changes is correct. It is the real correctness mechanism for `m2/03` (see Calibration in `../README.md`).

- [ ] **Step 1: Write the failing property test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Differential/IndexIncrementalEquivalenceTests.cs
using CsCheck;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Differential;

[Collection("postgres")]
public class IndexIncrementalEquivalenceTests(PostgresFixture fx)
{
    [Fact]
    public async Task Incrementally_maintained_index_matches_rebuild_and_oracle()
    {
        var gen = Gen.Select(ModelGenerator.SchemaSkeleton, IndexModelGenerators.WriteSequence,
            (schema, ops) => (schema, ops));

        await gen.SampleAsync(async pair =>
        {
            await using var h = await DifferentialHarness.SeedEmptyAsync(fx, pair.schema);

            // Apply each write through the normal path; incremental maintenance runs in-transaction.
            foreach (var op in pair.ops)
                await h.ApplyWriteAsync(op);          // writes tuple + runs IncrementalIndexMaintainer

            // Independent full rebuild into a scratch index for the same final state.
            await h.RebuildScratchIndexAsync();

            foreach (var (subject, type, permission) in h.ListProbes())
            {
                var oracle      = (await h.Oracle.ListObjectsAsync(h.ListReq(subject, type, permission))).ObjectIds.OrderBy(x => x).ToList();
                var incremental = (await h.IndexBacked.ListObjectsAsync(h.ListReq(subject, type, permission))).ObjectIds.OrderBy(x => x).ToList();
                var rebuilt     = (await h.ScratchIndexBacked.ListObjectsAsync(h.ListReq(subject, type, permission))).ObjectIds.OrderBy(x => x).ToList();

                incremental.ShouldBe(oracle);
                rebuilt.ShouldBe(oracle);
            }
        }, iter: 50);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexIncrementalEquivalenceTests`
Expected: FAIL — `ApplyWriteAsync`/`RebuildScratchIndexAsync`/`ScratchIndexBacked` not present.

- [ ] **Step 3: Add the write-application and scratch-rebuild helpers**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Differential/DifferentialHarness.Incremental.cs
using Relkit.Abstractions;

namespace Relkit.Storage.Postgres.Tests.Differential;

public partial class DifferentialHarness
{
    public IndexBackedAuthorizer ScratchIndexBacked { get; private set; } = default!;

    public async Task ApplyWriteAsync(WriteOp op)
    {
        await using var uow = await UowFactory.BeginAsync();
        var add = op.Add ? new[] { op.Tuple } : Array.Empty<RelationTuple>();
        var remove = op.Add ? Array.Empty<RelationTuple>() : new[] { op.Tuple };
        await RelationStore.WriteAsync(Tenant, add, remove, uow);
        await Maintainer.OnTuplesChangedAsync(Tenant, ActiveSchema, add, remove, uow);  // m2/03
        await uow.CommitAsync();
    }

    public async Task RebuildScratchIndexAsync()
    {
        await using var uow = await UowFactory.BeginAsync();
        await _scratchRebuilder.RebuildAsync(Tenant, ActiveSchema.Version, uow);   // writes to a second index table/namespace
        await uow.CommitAsync();
    }
}
```

> Show the `SeedEmptyAsync` wiring that constructs `Maintainer`, `_scratchRebuilder`, and `ScratchIndexBacked` (pointing at a separate `reverse_index` namespace/schema so the two indexes do not collide).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter IndexIncrementalEquivalenceTests`
Expected: PASS. Any failing CsCheck case prints the minimal shrunk write-sequence that broke incremental maintenance — fix `m2/03`, not this test.

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Differential
git commit -m "test: assert incremental reverse index equals rebuild and oracle"
```

---

## Self-review checklist (run after all tasks)

- [ ] Both rebuild and incremental paths are compared against the oracle, not just against each other.
- [ ] Write sequences exercise add/remove of `blocked` (exclusion) and arrow-reachable tuples.
- [ ] A shrunk counterexample points at `m2/03` (incremental maintenance), which is the code under test — this harness is the proof obligation for that plan.
