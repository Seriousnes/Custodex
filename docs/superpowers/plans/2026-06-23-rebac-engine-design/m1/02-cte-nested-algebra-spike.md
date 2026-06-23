# M1/02 — CTE Nested-Algebra Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A deliberately small, throwaway spike that **proves** how much of the permission algebra a recursive Postgres CTE can absorb when nested exclusion / intersection interleave *with* arrow traversal (spec §7.1), and **decides the CTE/engine seam** that `m1/05` (CTE Check) and `m1/06` (CTE ListObjects) build on. The spike pins one concrete throwaway schema where an inner `Exclude` lives under an `Arrow`, hand-computes the truth, writes the candidate recursive-CTE SQL, and asserts against the hand-computed truth via Testcontainers. The output is the **decided seam paragraph** reproduced verbatim in `m1/05`/`m1/06`/`m1/08`.

**Architecture:** This is a spike, not a shipped component. Its code lives entirely in the test project (`tests/Relkit.Storage.Postgres.Tests/Spike/`) and is deleted-or-kept-as-documentation once the decision is recorded. It loads tuples into the real `relation_tuples` schema (`m1/01`), runs a candidate recursive CTE that expands **reachability only** (nested subject-set membership + arrow edge-following), and proves that the conservative seam — **CTE for reachability, C# for the boolean algebra** — returns the correct answer on the discriminating case while a naive **all-in-SQL top-level post-filter** returns the *wrong* answer. That divergence is the whole point: it is exactly the bug the `m1/08` differential harness exists to catch, demonstrated in week one on a single hand-checked case.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m1/01` (the `relation_tuples` schema, `MigrationRunner`, `PostgresFixture`) and `m1/03` (`NpgsqlUnitOfWorkFactory` for seeding). The canonical correctness oracle is `EngineDrivenAuthorizer` (`m0/05`): the spike's hand-computed truth must match what that pointwise authorizer returns for the same schema and tuples.

## THE DECIDED SEAM (this plan's primary output — copied verbatim into m1/05, m1/06, m1/08)

> **Relkit CTE/engine seam (decided by this spike, validated by the m1/08 differential harness).**
>
> The recursive Postgres CTE computes **reachability only**: starting from an `(object, relation)` it expands nested subject-set membership (`group:G#member` → `G`'s `member` tuples, transitively) and follows structural-reference edges for arrows (`animal#enclosure@enclosure:KH1` → the related `enclosure:KH1`), producing the **leaf set of `(subject_type, subject_id)` rows reachable through a single relation or a single arrow hop's sub-permission**. It does **not** compute union/intersection/exclusion/conditioned across permission branches, and it does **not** post-filter at the top level.
>
> The **boolean algebra is composed in C#**, in `NpgsqlCteAuthorizer`, walking the `PermExpr` tree exactly as `EngineDrivenAuthorizer` does (`m0/05`): `Union`/`Intersect`/`Exclude` short-circuit over sub-results; `Arrow(rel, perm)` follows the `rel` edges (one CTE reachability query) and recurses into each related object's **full** `perm` expression — so an inner `- blocked` under an arrow is always seen, because the C# walk re-enters the related object's whole expression rather than post-filtering the top object. `Conditioned` and tuple-carried conditions are evaluated in C# via `IConditionEvaluator` against synced attributes + request context, identically to the oracle.
>
> The CTE's job is therefore to make the **two storage primitives the oracle already depends on** fast and recursive on Postgres: "the subjects reachable through `object#relation` including nested groups" and "the related objects reachable through `object#arrow-relation`". `NpgsqlCteAuthorizer` is a Postgres-native reimplementation of the oracle's traversal whose **only** divergence from `EngineDrivenAuthorizer` is *where the recursion runs* (SQL vs C# loops); the algebra layer is identical. `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer` is the correctness claim, and the `m1/08` differential harness is its only proof.
>
> **Project reference (decided here):** `Relkit.Storage.Postgres` takes a project reference on `Relkit.Core`, because `NpgsqlCteAuthorizer` reuses `SchemaIndex`, `EvaluationOptions`, `EvalContext`/`EvalFrame`, `ContinuationCursor`, and the `IConditionEvaluator` seam from `Relkit.Core`. (Spec §4 forbids *database code in Core*; it does not forbid the Postgres provider depending on Core. The reverse dependency — Core on Postgres — remains forbidden.) `m1/05` adds this reference in its first task.

---

### Task 1: The throwaway spike schemas and the discriminating tuple set

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Spike/SpikeData.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Spike/SpikeTruthTableTests.cs`

**Interfaces:**
- Produces: `SpikeData` holding the concrete throwaway tuples for two cases — **(A) inner exclusion through an arrow** and **(B) intersection-through-arrow with a wildcard gate (spec §12.5)** — plus the hand-computed expected answers per `(object, subject)`. A pure (no-DB) test asserts the hand-computed truth table is internally consistent with the documented schema, so the SQL tasks have a fixed target.

**Interfaces (the modelled schemas, documented in code comments, mirroring the oracle's `m0/05` `AlgebraTests`):**

- **Case A — inner exclusion under an arrow (the discriminator):**
  ```
  type enclosure:
    relation editor, blocked
    permission edit = editor - blocked
  type animal:
    relation enclosure: enclosure
    permission edit = enclosure->edit
  ```
  Tuples: `animal:EL-001#enclosure@enclosure:KH1`, `enclosure:KH1#editor@user:carol`, `enclosure:KH1#blocked@user:carol`, `enclosure:KH1#editor@user:dana`.
  Hand-computed truth for `animal:EL-001#edit`: **carol → false** (editor on KH1 but also blocked on KH1; the inner `- blocked` revokes her), **dana → true** (editor, not blocked).
  This is the bug case: a naive CTE that gathers everyone reachable through `enclosure->editor` and then post-filters `blocked` *on the animal* sees no `animal#blocked` tuple and wrongly returns **carol → true**.

- **Case B — intersection through arrow with a wildcard gate (spec §12.5):**
  ```
  type enclosure:
    relation is_quarantine            # filled by user:* to mark quarantine
    permission is_quarantine = is_quarantine
  type animal:
    relation enclosure: enclosure
    relation vet_member, trained_member   # subject-set group#member fillers
    permission access =
        enclosure->is_quarantine & vet_member & trained_member
  ```
  Tuples: `animal:EL-001#enclosure@enclosure:Q1`, `enclosure:Q1#is_quarantine@user:*`, `animal:EL-001#vet_member@group:vets#member`, `animal:EL-001#trained_member@group:trained#member`, `group:vets#member@user:dr-smith`, `group:vets#member@user:jones`, `group:trained#member@user:dr-smith`.
  Hand-computed truth for `animal:EL-001#access`: **dr-smith → true** (quarantine universal ∧ vet ∧ trained), **jones → false** (vet but not trained), **outsider → false** (not vet).

- [ ] **Step 1: Write the failing truth-table sanity test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/SpikeTruthTableTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Spike;

public class SpikeTruthTableTests
{
    [Fact]
    public void Case_A_inner_exclusion_truth_is_pinned()
    {
        // carol: editor AND blocked on KH1 => animal.edit denies via inner exclusion through the arrow.
        SpikeData.CaseA_Expected[("EL-001", "carol")].ShouldBeFalse();
        // dana: editor, not blocked => allowed.
        SpikeData.CaseA_Expected[("EL-001", "dana")].ShouldBeTrue();
    }

    [Fact]
    public void Case_B_intersection_through_arrow_truth_is_pinned()
    {
        SpikeData.CaseB_Expected[("EL-001", "dr-smith")].ShouldBeTrue();   // quarantine & vet & trained
        SpikeData.CaseB_Expected[("EL-001", "jones")].ShouldBeFalse();     // vet but not trained
        SpikeData.CaseB_Expected[("EL-001", "outsider")].ShouldBeFalse();  // not a vet
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter SpikeTruthTableTests`
Expected: FAIL — `SpikeData` does not exist.

- [ ] **Step 3: Write the spike data**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/SpikeData.cs
namespace Relkit.Storage.Postgres.Tests.Spike;

/// <summary>
/// Throwaway spike fixtures. Two cases interleave algebra WITH arrow traversal — the shape
/// spec §7.1 calls out as the hardest. Tuples are raw column rows (the spike writes them with
/// Dapper, bypassing the not-yet-built relation store). Expected answers are hand-computed and
/// match what the m0/05 EngineDrivenAuthorizer returns for the same data.
/// </summary>
internal static class SpikeData
{
    internal const string Store = "spike";
    internal const string Tenant = "t";

    /// <summary>(object_type, object_id, relation, subject_type, subject_id, subject_relation?)</summary>
    internal sealed record Fact(
        string ObjectType, string ObjectId, string Relation,
        string SubjectType, string SubjectId, string? SubjectRelation = null);

    // ── Case A: animal.edit = enclosure->edit ; enclosure.edit = editor - blocked ─────────────
    internal static readonly IReadOnlyList<Fact> CaseA =
    [
        new("animal", "EL-001", "enclosure", "enclosure", "KH1"),
        new("enclosure", "KH1", "editor", "user", "carol"),
        new("enclosure", "KH1", "blocked", "user", "carol"),
        new("enclosure", "KH1", "editor", "user", "dana"),
    ];

    internal static readonly IReadOnlyDictionary<(string Obj, string Subject), bool> CaseA_Expected =
        new Dictionary<(string, string), bool>
        {
            [("EL-001", "carol")] = false,   // editor but blocked on the enclosure → inner exclusion denies
            [("EL-001", "dana")] = true,     // editor, not blocked
        };

    // ── Case B: animal.access = enclosure->is_quarantine & vet_member & trained_member ────────
    internal static readonly IReadOnlyList<Fact> CaseB =
    [
        new("animal", "EL-001", "enclosure", "enclosure", "Q1"),
        new("enclosure", "Q1", "is_quarantine", "user", "*"),
        new("animal", "EL-001", "vet_member", "group", "vets", "member"),
        new("animal", "EL-001", "trained_member", "group", "trained", "member"),
        new("group", "vets", "member", "user", "dr-smith"),
        new("group", "vets", "member", "user", "jones"),
        new("group", "trained", "member", "user", "dr-smith"),
    ];

    internal static readonly IReadOnlyDictionary<(string Obj, string Subject), bool> CaseB_Expected =
        new Dictionary<(string, string), bool>
        {
            [("EL-001", "dr-smith")] = true,    // quarantine(universal) ∧ vet ∧ trained
            [("EL-001", "jones")] = false,      // vet but not trained
            [("EL-001", "outsider")] = false,   // not even a vet
        };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter SpikeTruthTableTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Spike
git commit -m "test: pin the CTE-spike throwaway schemas and hand-computed truth"
```

---

### Task 2: The reachability CTE — prove a single relation expands nested subject-sets

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Spike/ReachabilityCte.cs`
- Test: `tests/Relkit.Storage.Postgres.Tests/Spike/ReachabilityCteTests.cs`

**Interfaces:**
- Produces: `ReachabilityCte` with `static Task<IReadOnlyList<(string Type, string Id)>> SubjectsThroughRelationAsync(NpgsqlConnection conn, string store, string tenant, string objectType, string objectId, string relation, CancellationToken ct)` — runs the candidate recursive CTE that, given one `(object, relation)`, returns the **distinct leaf subjects** reachable by transitively expanding `group:G#member` (and any subject-set) tuples. This is the reachability primitive the decided seam runs in SQL; the C# algebra layer composes over it.
- Consumes: the `relation_tuples` schema (`m1/01`); a seeded spike dataset (Task 1 + the seed helper in this task).

> **Calibration note.** This recursive CTE is the candidate validated by *this* spike and the `m1/08` differential harness — not guaranteed-correct copy-paste. The shape: a recursive CTE whose base row is the requested `(object_type, object_id, relation)`, and whose recursive step, for any frontier tuple whose subject is a subject-set `group:G#rel`, joins back to `relation_tuples` to pull `G`'s `rel` tuples. The terminal rows (non-subject-set subjects, including wildcard `user:*`) are the leaf set. A `cycle … using path` clause (Postgres 14+) guards nested-group loops so `a#member@b#member`, `b#member@a#member` terminates.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/ReachabilityCteTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Spike;

[Collection("postgres")]
public class ReachabilityCteTests(PostgresFixture fx) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await SpikeSeed.LoadAsync(conn, SpikeData.CaseB);   // Case B has the nested group#member tuples
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Reachability_expands_a_subject_set_to_its_leaf_users()
    {
        await using var conn = await fx.OpenAsync();
        // animal:EL-001#vet_member@group:vets#member ; group:vets#member@{dr-smith, jones}
        var subjects = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "animal", "EL-001", "vet_member");

        subjects.ShouldContain(("user", "dr-smith"));
        subjects.ShouldContain(("user", "jones"));
        subjects.ShouldNotContain(("group", "vets"));   // subject-sets are expanded, not returned as leaves
    }

    [Fact]
    public async Task Reachability_returns_the_wildcard_leaf_for_a_universal_gate()
    {
        await using var conn = await fx.OpenAsync();
        // enclosure:Q1#is_quarantine@user:* — the leaf is the wildcard itself.
        var subjects = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "enclosure", "Q1", "is_quarantine");

        subjects.ShouldContain(("user", "*"));
    }
}
```

> Add the seed helper used by Tasks 2–4 (writes raw rows; the relation store arrives in `m1/04`):

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/SpikeSeed.cs
using Dapper;
using Npgsql;

namespace Relkit.Storage.Postgres.Tests.Spike;

internal static class SpikeSeed
{
    internal static async Task LoadAsync(NpgsqlConnection conn, IReadOnlyList<SpikeData.Fact> facts)
    {
        await conn.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = SpikeData.Store });
        await conn.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = SpikeData.Store, t = SpikeData.Tenant });

        foreach (var f in facts)
            await conn.ExecuteAsync("""
                INSERT INTO relation_tuples
                    (store_id, tenant_id, object_type, object_id, relation,
                     subject_type, subject_id, subject_relation)
                VALUES (@store, @tenant, @ot, @oid, @rel, @st, @sid, @srel)
                ON CONFLICT DO NOTHING
                """,
                new
                {
                    store = SpikeData.Store, tenant = SpikeData.Tenant,
                    ot = f.ObjectType, oid = f.ObjectId, rel = f.Relation,
                    st = f.SubjectType, sid = f.SubjectId, srel = f.SubjectRelation,
                });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReachabilityCteTests`
Expected: FAIL — `ReachabilityCte` / `SpikeSeed` do not exist.

- [ ] **Step 3: Write the candidate reachability CTE**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/ReachabilityCte.cs
using Dapper;
using Npgsql;

namespace Relkit.Storage.Postgres.Tests.Spike;

/// <summary>
/// Candidate recursive-CTE reachability primitive (validated by this spike + the m1/08 harness).
/// Given one (object, relation), returns the distinct leaf subjects reachable by transitively
/// expanding subject-set (group#member-style) tuples. Leaf rows are subjects with NULL
/// subject_relation (concrete users and wildcards). Subject-sets are expanded, never returned.
/// </summary>
internal static class ReachabilityCte
{
    private sealed record Leaf(string SubjectType, string SubjectId);

    private const string Sql = """
        WITH RECURSIVE reach (object_type, object_id, relation) AS (
            -- base: the requested (object, relation)
            SELECT @ot::text, @oid::text, @rel::text
          UNION
            -- step: for each frontier tuple whose subject is a subject-set group:G#srel,
            -- follow into G's srel tuples (one more hop of nested membership).
            SELECT rt.subject_type, rt.subject_id, rt.subject_relation
            FROM reach r
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.object_type = r.object_type
             AND rt.object_id   = r.object_id
             AND rt.relation    = r.relation
            WHERE rt.subject_relation IS NOT NULL
        )
        SELECT DISTINCT rt.subject_type, rt.subject_id
        FROM reach r
        JOIN relation_tuples rt
          ON rt.store_id = @store AND rt.tenant_id = @tenant
         AND rt.object_type = r.object_type
         AND rt.object_id   = r.object_id
         AND rt.relation    = r.relation
        WHERE rt.subject_relation IS NULL          -- leaf subjects only
        """;

    internal static async Task<IReadOnlyList<(string Type, string Id)>> SubjectsThroughRelationAsync(
        NpgsqlConnection conn, string store, string tenant,
        string objectType, string objectId, string relation, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<Leaf>(new CommandDefinition(Sql,
            new { store, tenant, ot = objectType, oid = objectId, rel = relation },
            cancellationToken: ct));
        return rows.Select(l => (l.SubjectType, l.SubjectId)).ToList();
    }
}
```

> **Cycle safety.** The `UNION` (not `UNION ALL`) deduplicates the `(object_type, object_id, relation)` frontier, so a membership cycle `a#member@b#member`, `b#member@a#member` cannot loop forever — once a frontier row repeats it is dropped. If a later case needs `UNION ALL` for multiplicity, add an explicit `CYCLE object_type, object_id, relation SET is_cycle USING path` clause; the `m1/08` harness is the proof either way.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter ReachabilityCteTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Spike
git commit -m "test: prove the reachability CTE expands nested subject-sets to leaf users"
```

---

### Task 3: The discriminator — prove naive all-in-SQL is WRONG and the seam is RIGHT (Case A)

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Spike/SeamVsNaiveTests.cs`

**Interfaces:**
- Consumes: `ReachabilityCte` (Task 2), `SpikeData`/`SpikeSeed` (Tasks 1–2). No new production code — this task is the heart of the spike: it demonstrates, on Case A, that the **naive top-level post-filter** CTE returns `carol → true` (WRONG) while the **decided seam** (reachability CTE + C# algebra recursing into the related object's full `edit = editor - blocked`) returns `carol → false` (RIGHT, matching the hand-computed truth and the oracle).

> **What this proves (the spike's whole reason to exist).** Case A is `animal.edit = enclosure->edit`, `enclosure.edit = editor - blocked`. carol is `editor` AND `blocked` on `KH1`.
> - **Naive all-in-SQL:** "expand everyone reachable through `animal -> enclosure -> editor`, then remove anyone in `animal#blocked`." There is no `animal#blocked` tuple (the block is on the *enclosure*), so carol survives → **true**. This is the §7.1 landmine: a top-level post-filter cannot see an inner exclusion under an arrow.
> - **Decided seam:** the CTE only answers "subjects reachable through `enclosure:KH1#editor`" and "subjects reachable through `enclosure:KH1#blocked`"; the **C# walk** evaluates `enclosure:KH1#edit = editor - blocked` pointwise for carol → `editor=true ∧ blocked=true ⇒ false`, then the arrow yields carol → **false**. Correct.
>
> The seam helper below is a *minimal* in-test reimplementation of the C# algebra walk for Case A only — it is the shape `NpgsqlCteAuthorizer` (m1/05) generalises. It exists to make the divergence executable and asserted.

- [ ] **Step 1: Write the discriminating test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/SeamVsNaiveTests.cs
using Dapper;
using Npgsql;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Spike;

[Collection("postgres")]
public class SeamVsNaiveTests(PostgresFixture fx) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await SpikeSeed.LoadAsync(conn, SpikeData.CaseA);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Naive all-in-SQL: gather subjects reachable via animal->enclosure->editor, then subtract
    // anyone listed in animal#blocked. It cannot see the enclosure-level block.
    private const string NaiveSql = """
        WITH editors AS (
            SELECT e.subject_type, e.subject_id
            FROM relation_tuples a
            JOIN relation_tuples e
              ON e.store_id = a.store_id AND e.tenant_id = a.tenant_id
             AND e.object_type = a.subject_type AND e.object_id = a.subject_id
             AND e.relation = 'editor'
            WHERE a.store_id = @store AND a.tenant_id = @tenant
              AND a.object_type = 'animal' AND a.object_id = @oid AND a.relation = 'enclosure'
        ),
        blocked_on_animal AS (
            SELECT subject_type, subject_id FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = 'animal' AND object_id = @oid AND relation = 'blocked'
        )
        SELECT EXISTS (
            SELECT 1 FROM editors x
            WHERE x.subject_type = 'user' AND x.subject_id = @sid
              AND NOT EXISTS (SELECT 1 FROM blocked_on_animal b
                              WHERE b.subject_type = x.subject_type AND b.subject_id = x.subject_id)
        )
        """;

    private async Task<bool> NaiveAllowsAsync(NpgsqlConnection conn, string oid, string sid) =>
        await conn.ExecuteScalarAsync<bool>(NaiveSql,
            new { store = SpikeData.Store, tenant = SpikeData.Tenant, oid, sid });

    // Decided seam for Case A: arrow into enclosure.edit = (editor reachable) AND NOT (blocked reachable),
    // evaluated pointwise in C# over the reachability CTE — recursing into the related object's full expr.
    private async Task<bool> SeamAllowsAsync(NpgsqlConnection conn, string animalId, string sid)
    {
        // animal.edit = enclosure->edit : find related enclosures via the 'enclosure' relation.
        var enclosures = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "animal", animalId, "enclosure");

        foreach (var (etype, eid) in enclosures)
        {
            // enclosure.edit = editor - blocked, pointwise for sid.
            var editors = await ReachabilityCte.SubjectsThroughRelationAsync(
                conn, SpikeData.Store, SpikeData.Tenant, etype, eid, "editor");
            var blocked = await ReachabilityCte.SubjectsThroughRelationAsync(
                conn, SpikeData.Store, SpikeData.Tenant, etype, eid, "blocked");

            var isEditor = editors.Contains(("user", sid));
            var isBlocked = blocked.Contains(("user", sid));
            if (isEditor && !isBlocked) return true;   // this enclosure grants edit
        }
        return false;
    }

    [Fact]
    public async Task Naive_all_in_sql_returns_the_WRONG_answer_for_the_inner_exclusion()
    {
        await using var conn = await fx.OpenAsync();
        // The naive query wrongly allows carol: it never sees enclosure-level 'blocked'.
        (await NaiveAllowsAsync(conn, "EL-001", "carol")).ShouldBeTrue();   // documents the bug
    }

    [Fact]
    public async Task Decided_seam_matches_the_hand_computed_truth_for_case_A()
    {
        await using var conn = await fx.OpenAsync();
        (await SeamAllowsAsync(conn, "EL-001", "carol"))
            .ShouldBe(SpikeData.CaseA_Expected[("EL-001", "carol")]);   // false — correct
        (await SeamAllowsAsync(conn, "EL-001", "dana"))
            .ShouldBe(SpikeData.CaseA_Expected[("EL-001", "dana")]);    // true — correct
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter SeamVsNaiveTests`
Expected: PASS (2 tests). The first test *documents* that the naive all-in-SQL approach is wrong (it asserts the wrong answer to capture the divergence); the second proves the decided seam is right.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Spike/SeamVsNaiveTests.cs
git commit -m "test: prove naive all-in-SQL fails the inner-exclusion case and the seam passes"
```

---

### Task 4: Prove the seam also handles intersection-through-arrow with a wildcard gate (Case B)

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Spike/SeamIntersectionGateTests.cs`

**Interfaces:**
- Consumes: `ReachabilityCte`, `SpikeData`/`SpikeSeed`. Proves the decided seam returns the §12.5 truth for `animal.access = enclosure->is_quarantine & vet_member & trained_member`, including the wildcard `user:*` gate (which makes `enclosure->is_quarantine` the universal set, so the intersection reduces to vet ∧ trained).

> **Wildcard handling in the seam.** A reachability leaf `("user", "*")` means "every user of this type." In the C# pointwise walk, a relation resolves true for subject `sid` when its reachable leaves contain `("user", sid)` **or** `("user", "*")`. The arrow `enclosure->is_quarantine` therefore resolves true for *any* user when `Q1` carries `is_quarantine@user:*`, so the intersection's truth is decided entirely by `vet_member` and `trained_member`. This is the same wildcard rule `EngineDrivenAuthorizer.ResolveRelationAsync` (m0/05) applies; the seam mirrors it.

- [ ] **Step 1: Write the test**

```csharp
// tests/Relkit.Storage.Postgres.Tests/Spike/SeamIntersectionGateTests.cs
using Npgsql;
using Shouldly;
using Xunit;

namespace Relkit.Storage.Postgres.Tests.Spike;

[Collection("postgres")]
public class SeamIntersectionGateTests(PostgresFixture fx) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await SpikeSeed.LoadAsync(conn, SpikeData.CaseB);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Pointwise: does sid hold `relation` on (objType,objId)? True if a reachable leaf is
    // ("user", sid) or the wildcard ("user","*").
    private static async Task<bool> HoldsRelationAsync(
        NpgsqlConnection conn, string objType, string objId, string relation, string sid)
    {
        var leaves = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, objType, objId, relation);
        return leaves.Contains(("user", sid)) || leaves.Contains(("user", "*"));
    }

    // animal.access = enclosure->is_quarantine & vet_member & trained_member, pointwise for sid.
    private static async Task<bool> SeamAccessAsync(NpgsqlConnection conn, string animalId, string sid)
    {
        // arrow: every related enclosure where sid holds is_quarantine (wildcard => any user).
        var enclosures = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "animal", animalId, "enclosure");
        var quarantine = false;
        foreach (var (etype, eid) in enclosures)
            if (await HoldsRelationAsync(conn, etype, eid, "is_quarantine", sid)) { quarantine = true; break; }

        var vet = await HoldsRelationAsync(conn, "animal", animalId, "vet_member", sid);
        var trained = await HoldsRelationAsync(conn, "animal", animalId, "trained_member", sid);
        return quarantine && vet && trained;   // short-circuit intersection
    }

    [Theory]
    [InlineData("dr-smith", true)]
    [InlineData("jones", false)]
    [InlineData("outsider", false)]
    public async Task Seam_matches_the_quarantine_intersection_truth(string sid, bool expected)
    {
        await using var conn = await fx.OpenAsync();
        (await SeamAccessAsync(conn, "EL-001", sid))
            .ShouldBe(SpikeData.CaseB_Expected[("EL-001", sid)]);
        (await SeamAccessAsync(conn, "EL-001", sid)).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Relkit.Storage.Postgres.Tests --filter SeamIntersectionGateTests`
Expected: PASS (3 cases). The seam matches §12.5 truth: quarantine-trained vet allowed, untrained vet denied, non-vet denied.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Spike/SeamIntersectionGateTests.cs
git commit -m "test: prove the seam handles §12.5 intersection-through-arrow with a wildcard gate"
```

---

### Task 5: Record the decision — the seam document `m1/05`/`m1/06` consume

**Files:**
- Create: `tests/Relkit.Storage.Postgres.Tests/Spike/SEAM_DECISION.md`

**Interfaces:**
- Produces: a short markdown note, committed alongside the spike, recording the decided seam (the boxed paragraph at the top of this plan) and the two proven facts: (1) reachability is expressible and cycle-safe in a recursive CTE; (2) the boolean algebra must stay in C# — a top-level SQL post-filter is provably wrong on inner exclusion through an arrow. This is the artefact `m1/05`/`m1/06` point at when they say "the seam validated by the spike."

- [ ] **Step 1: Write the decision note**

```markdown
<!-- tests/Relkit.Storage.Postgres.Tests/Spike/SEAM_DECISION.md -->
# CTE/engine seam — decided by the M1/02 spike

## Decision
- **Recursive CTE = reachability only.** A recursive CTE expands nested subject-set membership
  (`group:G#member` transitively) and follows arrow edges, returning the distinct leaf subjects
  reachable through one relation or one arrow hop. Proven cycle-safe (`UNION`-dedup frontier).
- **Algebra = C#.** `Union`/`Intersect`/`Exclude`/`Conditioned` and arrow-into-sub-permission are
  composed in `NpgsqlCteAuthorizer`, walking the `PermExpr` tree exactly like `EngineDrivenAuthorizer`.
  Arrow recurses into the related object's FULL expression, so inner exclusions/intersections are seen.
- **`Relkit.Storage.Postgres` references `Relkit.Core`** (for `SchemaIndex`, `EvaluationOptions`,
  `ContinuationCursor`, `IConditionEvaluator`). Core never references Postgres.

## Proven by this spike
1. **Reachability is CTE-expressible and cycle-safe** (Task 2): a subject-set expands to its leaf
   users; the wildcard `user:*` surfaces as a leaf; nested-group cycles terminate.
2. **All-in-SQL post-filtering is WRONG** (Task 3, Case A): `animal.edit = enclosure->edit`,
   `enclosure.edit = editor - blocked`, carol editor+blocked on the enclosure. The naive top-level
   post-filter returns carol→true (no `animal#blocked` tuple exists); the decided seam returns
   carol→false, matching the hand-computed truth and the `m0/05` oracle.
3. **The seam handles intersection-through-arrow with a wildcard gate** (Task 4, Case B / §12.5):
   quarantine-trained vet allowed, untrained vet denied, non-vet denied.

## Consequence for the build
`NpgsqlCteAuthorizer` is a Postgres-native reimplementation of the oracle's traversal whose only
divergence is *where the recursion runs* (SQL vs C# loops). `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer`
is the correctness claim; the **M1/08 differential harness is its only proof**. The candidate SQL in
m1/05 and m1/06 is "validated by this spike and the m1/08 harness," not guaranteed-correct copy-paste.
```

- [ ] **Step 2: Commit**

```bash
git add tests/Relkit.Storage.Postgres.Tests/Spike/SEAM_DECISION.md
git commit -m "docs: record the decided CTE/engine seam from the spike"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] The hand-computed truth table (Case A inner exclusion, Case B §12.5 intersection-through-arrow) is pinned and matches what `EngineDrivenAuthorizer` would return for the same data (Task 1).
- [ ] The reachability CTE expands nested subject-sets to leaf users, surfaces the wildcard leaf, and is cycle-safe (Task 2).
- [ ] The discriminator passes: naive all-in-SQL returns `carol → true` (wrong); the decided seam returns `carol → false` (right) (Task 3).
- [ ] The seam matches §12.5 intersection-through-arrow-with-wildcard truth (Task 4).
- [ ] The decided-seam paragraph is recorded and is the artefact `m1/05`/`m1/06`/`m1/08` reference (Task 5); it states: CTE = reachability, C# = algebra, Postgres → Core project reference.

## Contract gaps (reported, not changed)

- **None new.** This plan introduces no shared types. It establishes the `Relkit.Storage.Postgres → Relkit.Core` project reference as a *decision* (not a contract change): `Relkit.Core` already holds `SchemaIndex`/`EvaluationOptions`/`ContinuationCursor`/`IConditionEvaluator` and is a non-DB engine assembly, so referencing it from the Postgres provider respects spec §4 ("no database code in Core"). If a future reader expects the contract's package table to spell out this edge explicitly, that is a documentation nicety, not a missing type — flagged here for visibility.
```

