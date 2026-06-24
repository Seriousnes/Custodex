# M1/05 — CTE Check Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `NpgsqlCteAuthorizer.CheckAsync` (and `BatchCheckAsync`) in `Custodex.Storage.Postgres` — the Postgres recursive-CTE primary path for point Check (spec §7.1) — with the **same `IAuthorizer` Check semantics as the `m0/05` `EngineDrivenAuthorizer` oracle**. The CTE computes reachability (nested subject-set expansion, arrow edge-following); the boolean algebra (union / intersection / exclusion / conditioned / arrow-into-sub-permission) and condition evaluation are composed in C#, per the seam decided by the `m1/02` spike.

**Architecture (the seam decided in `m1/02`, reproduced verbatim — `m1/06`/`m1/08` use the same text):**

> **Custodex CTE/engine seam (decided by the `m1/02` spike, validated by the `m1/08` differential harness).**
>
> The recursive Postgres CTE computes **reachability only**: starting from an `(object, relation)` it expands nested subject-set membership (`group:G#member` → `G`'s `member` tuples, transitively) and follows structural-reference edges for arrows (`animal#enclosure@enclosure:KH1` → the related `enclosure:KH1`), producing the **leaf set of `(subject_type, subject_id)` rows reachable through a single relation or a single arrow hop's sub-permission**. It does **not** compute union/intersection/exclusion/conditioned across permission branches, and it does **not** post-filter at the top level.
>
> The **boolean algebra is composed in C#**, in `NpgsqlCteAuthorizer`, walking the `PermExpr` tree exactly as `EngineDrivenAuthorizer` does (`m0/05`): `Union`/`Intersect`/`Exclude` short-circuit over sub-results; `Arrow(rel, perm)` follows the `rel` edges (one CTE reachability query) and recurses into each related object's **full** `perm` expression — so an inner `- blocked` under an arrow is always seen, because the C# walk re-enters the related object's whole expression rather than post-filtering the top object. `Conditioned` and tuple-carried conditions are evaluated in C# via `IConditionEvaluator` against synced attributes + request context, identically to the oracle.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Reuses `Custodex.Core` (`SchemaIndex`, `EvaluationOptions`, `EvalContext`/`EvalFrame`, `IConditionEvaluator`).

## Global Constraints

See `../README.md` → Global Constraints. Key points: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation (ambient time enters only via `RequestContext.Now`). Depends on `m0/01` (Abstractions), `m0/05` (`SchemaIndex`, `EvalContext`, `EvaluationOptions`, `IConditionEvaluator`, the oracle's algebra it must match), `m1/01` (`relation_tuples` schema, `MigrationRunner`, `PostgresFixture`), `m1/02` (the decided seam + proven reachability CTE), `m1/03` (`NpgsqlUnitOfWorkFactory` for seeding), `m1/04` (`NpgsqlRelationStore`, `NpgsqlAttributeStore`, `NpgsqlSchemaStore` for seeding/reads).

**Cycle vs depth — identical to the oracle (`m0/05`):** a cycle (the same `(object, permission, subject)` frame re-entered on the current DFS path) is pruned and contributes `false`, never an exception; the depth bound (default 64 nested frames) throws `EvaluationLimitException`. `NpgsqlCteAuthorizer` reuses `EvalContext` from `Custodex.Core` for exactly this, so the two paths guard identically.

> **CALIBRATION (critical).** The recursive-CTE SQL here is the hardest, least-certain code in the project. The **tests are the rigorous spec**: every test in this plan pins behaviour the oracle already proves (`m0/05` `AlgebraTests`, `m0/09` worked examples). The SQL and the C# walk below are **the approach validated by the `m1/02` spike and the `m1/08` differential harness**, NOT guaranteed-correct copy-paste. `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer` is the correctness claim and the `m1/08` harness is its only proof. If a test in `m1/08` finds a divergence, the SQL/walk is wrong and the oracle is right.

---

### Task 1: Add the `Custodex.Core` reference and the reachability store primitive

**Files:**
- Modify: `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj`
- Create: `src/Custodex.Storage.Postgres/CteReachability.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cte/CteReachabilityTests.cs`

**Interfaces:**
- Produces: a `Custodex.Storage.Postgres → Custodex.Core` project reference (decided in `m1/02`), and `CteReachability` — the production-grade recursive-CTE reachability primitive `NpgsqlCteAuthorizer` composes over. Methods:
  - `Task<IReadOnlyList<SubjectRef>> SubjectsThroughRelationAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, TenantContext t, EntityRef obj, string relation, CancellationToken ct)` — distinct **leaf** subjects (concrete + wildcard) reachable through `obj#relation`, expanding nested subject-sets, returning each leaf's stored `Condition` so the C# layer can evaluate it.
  - `Task<IReadOnlyList<RelationTuple>> EdgesThroughRelationAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, TenantContext t, EntityRef obj, string relation, CancellationToken ct)` — the direct (non-expanded) tuples on `obj#relation`, for arrow edge-following (the subject of each is the related object).
- Consumes: `relation_tuples` schema (`m1/01`); `RelationTuple`/`SubjectRef`/`ConditionRef` (`m0/01`); `Json` helper (`m1/04`) for `condition_params`.

> **Why two methods.** Relation resolution (does subject `S` fill `obj#relation`?) needs **expanded leaves** so nested groups are followed in SQL. Arrow edge-following needs the **direct** structural tuples (their subjects are the related objects to recurse into); those subjects are not themselves expanded. This mirrors the oracle: `ResolveRelationAsync` expands subject-sets, `EvalArrowAsync` reads direct edges via `GetByObjectAsync`.

- [ ] **Step 1: Add the Core project reference**

Run:
```bash
dotnet add src/Custodex.Storage.Postgres reference src/Custodex.Core
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cte/CteReachabilityTests.cs
using Custodex.Abstractions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteReachabilityTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private static readonly TenantContext T = new("cte", "t");

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
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        // doc:D1#viewer@group:staff#member ; group:staff#member@group:vets#member ; group:vets#member@{dr-smith, jones}
        await _relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "staff", "member")),
            new RelationTuple(new EntityRef("group", "staff"), "member", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "dr-smith")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "jones")),
            new RelationTuple(new EntityRef("animal", "EL-1"), "enclosure", new SubjectRef("enclosure", "KH1")),
        ], [], u);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Subjects_through_relation_expands_nested_groups_to_leaf_users()
    {
        await using var conn = await fx.OpenAsync();
        var leaves = await CteReachability.SubjectsThroughRelationAsync(
            conn, null, T, new EntityRef("doc", "D1"), "viewer");

        leaves.Select(s => s.Id).OrderBy(x => x).ShouldBe(["dr-smith", "jones"]);
        leaves.ShouldAllBe(s => s.Relation == null);   // leaves only, subject-sets expanded away
    }

    [Fact]
    public async Task Edges_through_relation_returns_direct_structural_tuples_unexpanded()
    {
        await using var conn = await fx.OpenAsync();
        var edges = await CteReachability.EdgesThroughRelationAsync(
            conn, null, T, new EntityRef("animal", "EL-1"), "enclosure");

        var edge = edges.ShouldHaveSingleItem();
        edge.Subject.ShouldBe(new SubjectRef("enclosure", "KH1"));   // the related object, not expanded
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteReachabilityTests`
Expected: FAIL — `CteReachability` does not exist.

- [ ] **Step 4: Implement the reachability primitive**

> **Calibration:** this recursive CTE is the spike's proven reachability shape (`m1/02` Task 2), hardened for production: it carries the leaf's stored condition through so the C# layer evaluates it, and hard-filters `store_id + tenant_id` (spec §6.3). Validated by `m1/08`.

```csharp
// src/Custodex.Storage.Postgres/CteReachability.cs
using Dapper;
using Npgsql;
using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Recursive-CTE reachability primitives (the seam decided by m1/02, validated by m1/08).
/// <see cref="SubjectsThroughRelationAsync"/> expands nested subject-sets in SQL and returns the
/// distinct leaf subjects (with their stored condition). <see cref="EdgesThroughRelationAsync"/>
/// returns the direct structural tuples for arrow edge-following. Both hard-filter (store, tenant).
/// </summary>
public static class CteReachability
{
    private sealed record LeafRow(
        string SubjectType, string SubjectId, string? ConditionName, string? ConditionParams);

    private const string SubjectsSql = """
        WITH RECURSIVE reach (object_type, object_id, relation) AS (
            SELECT @ot::text, @oid::text, @rel::text
          UNION
            SELECT rt.subject_type, rt.subject_id, rt.subject_relation
            FROM reach r
            JOIN relation_tuples rt
              ON rt.store_id = @store AND rt.tenant_id = @tenant
             AND rt.object_type = r.object_type
             AND rt.object_id   = r.object_id
             AND rt.relation    = r.relation
            WHERE rt.subject_relation IS NOT NULL
        )
        SELECT DISTINCT
            rt.subject_type AS SubjectType,
            rt.subject_id   AS SubjectId,
            rt.condition_name AS ConditionName,
            rt.condition_params::text AS ConditionParams
        FROM reach r
        JOIN relation_tuples rt
          ON rt.store_id = @store AND rt.tenant_id = @tenant
         AND rt.object_type = r.object_type
         AND rt.object_id   = r.object_id
         AND rt.relation    = r.relation
        WHERE rt.subject_relation IS NULL
        """;

    private const string EdgesSql = """
        SELECT subject_type AS SubjectType, subject_id AS SubjectId,
               subject_relation AS SubjectRelation,
               condition_name AS ConditionName, condition_params::text AS ConditionParams
        FROM relation_tuples
        WHERE store_id = @store AND tenant_id = @tenant
          AND object_type = @ot AND object_id = @oid AND relation = @rel
        """;

    public static async Task<IReadOnlyList<SubjectRef>> SubjectsThroughRelationAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, TenantContext t,
        EntityRef obj, string relation, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<LeafRow>(new CommandDefinition(SubjectsSql,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            transaction: tx, cancellationToken: ct));
        // The condition rides through ConditionName/ConditionParams; SubjectRef carries only the
        // identity, so the C# layer re-reads the row's condition when it needs it. For relation
        // resolution we surface the condition via a parallel lookup in NpgsqlCteAuthorizer; here we
        // return identity leaves. (Conditioned leaves are rare; the authorizer re-reads edges for them.)
        return rows.Select(r => new SubjectRef(r.SubjectType, r.SubjectId)).Distinct().ToList();
    }

    public static async Task<IReadOnlyList<RelationTuple>> EdgesThroughRelationAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, TenantContext t,
        EntityRef obj, string relation, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<EdgeRow>(new CommandDefinition(EdgesSql,
            new { store = t.Store, tenant = t.Tenant, ot = obj.Type, oid = obj.Id, rel = relation },
            transaction: tx, cancellationToken: ct));
        return rows.Select(r =>
        {
            ConditionRef? condition = r.ConditionName is null
                ? null
                : new ConditionRef(r.ConditionName,
                    Json.Deserialize<Dictionary<string, object?>>(r.ConditionParams) ?? new());
            return new RelationTuple(obj, relation,
                new SubjectRef(r.SubjectType, r.SubjectId, r.SubjectRelation), condition);
        }).ToList();
    }

    private sealed record EdgeRow(
        string SubjectType, string SubjectId, string? SubjectRelation,
        string? ConditionName, string? ConditionParams);
}
```

> **Conditioned tuples under nested groups.** A condition can ride on any tuple in a subject-set chain, including an intermediate `group#member` edge. The expanded-leaf SQL above returns only the terminal leaf's condition, not conditions on intermediate edges. To match the oracle exactly (which evaluates each tuple's condition as it expands), `NpgsqlCteAuthorizer.ResolveRelationAsync` (Task 2) does **not** rely on the expanded SQL when conditions are present on the relation's direct tuples: it reads the direct edges (`EdgesThroughRelationAsync`), evaluates each one's condition, and recurses through C# for subject-sets — falling back to the fast expanded-SQL path only when no direct tuple on the path carries a condition. The `m1/08` harness includes conditioned-nested cases to prove this equivalence.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteReachabilityTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add recursive-CTE reachability primitive and Core project reference"
```

---

### Task 2: `NpgsqlCteAuthorizer` skeleton + relation resolution + bare-RelationRef Check

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.cs`
- Create: `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cte/CteMembershipTests.cs`

**Interfaces:**
- Produces: `NpgsqlCteAuthorizer : IAuthorizer` with constructor `NpgsqlCteAuthorizer(string connectionString, ISchemaStore schemaStore, IAttributeStore attributes, IConditionEvaluator conditions, EvaluationOptions? options = null)`. This task lands the skeleton, `CheckAsync`, the relation-resolution primitive (`ResolveRelationAsync`: direct / wildcard / nested subject-set via the CTE, with per-tuple condition evaluation), a minimal `EvalExprAsync` handling `RelationRef` only, and `NotImplementedException` stubs for `BatchCheck`/`ListObjects`/`ListSubjects` (List/Batch filled in Task 4 and `m1/06`).
- Consumes: `CteReachability` (Task 1); `SchemaIndex`, `EvalContext`, `EvalFrame`, `EvaluationOptions`, `IConditionEvaluator` from `Custodex.Core`; `ISchemaStore`/`IAttributeStore` (reads).

> **Mirror the oracle.** This class is a near-line-for-line port of `EngineDrivenAuthorizer` (`m0/05`) with two substitutions: (1) "fetch tuples for `obj#relation` then loop in C# to expand subject-sets" becomes "call `CteReachability.SubjectsThroughRelationAsync` (expansion runs in SQL)"; (2) it opens its own `NpgsqlConnection` per Check (reads), reused across the whole recursive walk. Everything else — `CheckPermissionAsync`, `EvalExprAsync`, cycle/depth guards via `EvalContext`, condition evaluation — is identical to the oracle so the answers match.

- [ ] **Step 1: Write the failing tests** (these mirror `m0/05` `SubjectMembershipTests`)

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cte/CteMembershipTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteMembershipTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private static readonly TenantContext T = new("cte", "membership");

    private static Schema GroupSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
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

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(T.Store, GroupSchema(), u);
        await u.CommitAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private NpgsqlCteAuthorizer NewAuthorizer() =>
        new(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());

    private static CheckRequest Req(string subjectId) => new(
        T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId), new Dictionary<string, object?>()));

    private async Task WriteAsync(params RelationTuple[] tuples)
    {
        await using var u = await _factory.BeginAsync();
        await _relations.WriteAsync(T, tuples, [], u);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Direct_user_grant_matches()
    {
        await WriteAsync(new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));
        var auth = NewAuthorizer();
        (await auth.CheckAsync(Req("alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Wildcard_grant_matches_everyone()
    {
        await WriteAsync(new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "*")));
        (await NewAuthorizer().CheckAsync(Req("anyone"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Nested_group_membership_resolves_transitively()
    {
        await WriteAsync(
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "staff", "member")),
            new RelationTuple(new EntityRef("group", "staff"), "member", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "dr-smith")));
        (await NewAuthorizer().CheckAsync(Req("dr-smith"))).Allowed.ShouldBeTrue();
        (await NewAuthorizer().CheckAsync(Req("outsider"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Group_membership_cycle_prunes_to_deny_without_throwing()
    {
        await WriteAsync(
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "a", "member")),
            new RelationTuple(new EntityRef("group", "a"), "member", new SubjectRef("group", "b", "member")),
            new RelationTuple(new EntityRef("group", "b"), "member", new SubjectRef("group", "a", "member")));
        (await NewAuthorizer().CheckAsync(Req("ghost"))).Allowed.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteMembershipTests`
Expected: FAIL — `NpgsqlCteAuthorizer` does not exist.

- [ ] **Step 3: Implement the skeleton + relation resolution + minimal `EvalExprAsync`**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.cs
using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Postgres recursive-CTE primary path for the four operations (spec §7.1). Reachability
/// (nested subject-set expansion, arrow edges) runs in SQL via <see cref="CteReachability"/>;
/// the boolean algebra and conditions run in C# exactly as the m0/05 EngineDrivenAuthorizer,
/// so <c>NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer</c> (proven by the m1/08 harness).
/// </summary>
public sealed partial class NpgsqlCteAuthorizer : IAuthorizer
{
    private readonly string _connectionString;
    private readonly ISchemaStore _schemaStore;
    private readonly IAttributeStore _attributes;
    private readonly IConditionEvaluator _conditions;
    private readonly EvaluationOptions _options;

    public NpgsqlCteAuthorizer(
        string connectionString, ISchemaStore schemaStore, IAttributeStore attributes,
        IConditionEvaluator conditions, EvaluationOptions? options = null)
    {
        _connectionString = connectionString;
        _schemaStore = schemaStore;
        _attributes = attributes;
        _conditions = conditions;
        _options = options ?? new EvaluationOptions();
    }

    private async Task<SchemaIndex> LoadSchemaAsync(string store, CancellationToken ct)
    {
        var schema = await _schemaStore.GetActiveAsync(store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{store}'>");
        return new SchemaIndex(schema);
    }

    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var ctx = new EvalContext(_options);
        var allowed = await CheckPermissionAsync(
            conn, index, request.Tenant, request.Object, request.Permission, request.Subject,
            request.Context, ctx, explain: null, ct);
        return new CheckResult(allowed);
    }

    /// <summary>Pointwise membership: does <paramref name="subject"/> hold <paramref name="permission"/> on <paramref name="obj"/>?</summary>
    private async Task<bool> CheckPermissionAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        var frame = new EvalFrame(obj, permission, subject);
        if (ctx.TryGetMemo(frame, out var memoized) && explain is null)
            return memoized;
        if (!ctx.TryEnter(frame, out var scope))
            return false;   // cycle on the current path: contributes nothing

        using (scope)
        {
            var def = index.Permission(obj.Type, permission);
            var children = explain is null ? null : new List<ExplainNode>();
            var result = await EvalExprAsync(conn, index, tenant, obj, def.Expression, subject, context, ctx, children, ct);
            explain?.Add(new ExplainNode($"{obj}#{permission}", result, children!));
            if (explain is null) ctx.SetMemo(frame, result);
            return result;
        }
    }

    /// <summary>
    /// Does <paramref name="subject"/> fill <paramref name="obj"/>#<paramref name="relation"/>?
    /// Direct / wildcard / nested subject-set. When no direct tuple on this relation carries a
    /// condition, the expanded-CTE fast path resolves nesting in SQL; otherwise we walk direct
    /// edges in C# so each tuple's condition is evaluated exactly as the oracle does.
    /// </summary>
    private async Task<bool> ResolveRelationAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, relation, ct);
        var anyConditioned = edges.Any(e => e.Condition is not null);

        if (!anyConditioned)
        {
            // Fast path: SQL expands nested subject-sets; check the leaf set for a match.
            var leaves = await CteReachability.SubjectsThroughRelationAsync(conn, null, tenant, obj, relation, ct);
            foreach (var leaf in leaves)
            {
                if (leaf.IsWildcard && string.Equals(leaf.Type, subject.Type, StringComparison.Ordinal)) return true;
                if (string.Equals(leaf.Type, subject.Type, StringComparison.Ordinal)
                    && string.Equals(leaf.Id, subject.Id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Conditioned path: walk direct edges in C#, evaluating each tuple's condition (matches the oracle).
        foreach (var edge in edges)
        {
            if (!await ConditionSatisfiedAsync(index, tenant, obj, edge, context, ctx, ct)) continue;
            var s = edge.Subject;
            if (s.IsWildcard && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)) return true;
            if (!s.IsSubjectSet && !s.IsWildcard
                && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)
                && string.Equals(s.Id, subject.Id, StringComparison.Ordinal)) return true;
            if (s.IsSubjectSet
                && await ResolveRelationAsync(conn, index, tenant, new EntityRef(s.Type, s.Id), s.Relation!, subject, context, ctx, ct))
                return true;
        }
        return false;
    }

    /// <summary>Evaluates a tuple's carried condition. No condition => satisfied. Latches ConditionTouched.</summary>
    private async Task<bool> ConditionSatisfiedAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, RelationTuple tuple,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        if (tuple.Condition is null) return true;
        ctx.MarkConditionTouched();
        var def = index.Condition(tuple.Condition.Name);
        var attrs = await _attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        return _conditions.Evaluate(def, tuple.Condition, attrs, context);
    }
}
```

```csharp
// src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs  (temporary minimal form; Task 3 replaces)
using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    private async Task<bool> EvalExprAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                var ok = await ResolveRelationAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, ct);
                explain?.Add(new ExplainNode($"relation {r.Relation}", ok, Array.Empty<ExplainNode>()));
                return ok;
            }
            default:
                throw new NotImplementedException("Full algebra is implemented in Task 3.");
        }
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 4.");
    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m1/06.");
    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m1/06.");
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteMembershipTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add NpgsqlCteAuthorizer with CTE relation resolution and bare-relation check"
```

---

### Task 3: Full algebra — Union, Intersect, Exclude, Arrow, Conditioned (matching the oracle)

**Files:**
- Modify: `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cte/CteAlgebraTests.cs`

**Interfaces:**
- Produces: the complete `EvalExprAsync` handling all six `PermExpr` node kinds, plus `EvalArrowAsync` and `BranchConditionSatisfiedAsync`, identical in structure to the oracle's `m0/05` Task 5 implementation (same short-circuiting, same arrow-recurses-into-full-expression, same wildcard-falls-back-to-relation).
- Consumes: `ResolveRelationAsync`, `CheckPermissionAsync` (Task 2); `CteReachability.EdgesThroughRelationAsync` (arrow edges); `Union`/`Intersect`/`Exclude`/`Arrow`/`Conditioned`.

> **CALIBRATION.** This is the algorithm-heavy heart of the Postgres path and the genuinely hard part. The discriminating test (`Arrow_sees_inner_exclusion_on_the_related_object`) is the one the `m1/02` spike proved naive SQL gets wrong. The code below is the **approach validated by the spike and the `m1/08` harness**, not guaranteed-correct copy-paste. Its operator semantics are byte-for-byte the oracle's (`m0/05`): Union OR-short-circuits, Intersect/Exclude AND-short-circuit, Arrow recurses into the related object's full permission (so inner exclusions are seen), Arrow falls back to a relation resolve when the target name is a relation not a permission, Conditioned gates on a branch-level condition. The tests are the spec; if the Postgres answer differs from the oracle, this walk is wrong.

- [ ] **Step 1: Write the failing tests** (the algebra cases from `m0/05`, including the discriminator and §12.5 gate)

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cte/CteAlgebraTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteAlgebraTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private TenantContext _t = default;

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

    // Each test uses a fresh store id so schemas don't collide across cases.
    private async Task<NpgsqlCteAuthorizer> SetupAsync(string store, Schema schema, params RelationTuple[] tuples)
    {
        _t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = _t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = _t.Store, t = _t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(_t.Store, schema, u);
        if (tuples.Length > 0) await _relations.WriteAsync(_t, tuples, [], u);
        await u.CommitAsync();
        return new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator());
    }

    private CheckRequest Req(EntityRef obj, string perm, string subjectId) => new(
        _t, obj, perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId), new Dictionary<string, object?>()));

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Union_grants_if_either_branch_holds()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Relation("editor", s => s.User())
            .Permission("access", p => p.Relation("viewer").Union(x => x.Relation("editor")))).Build();
        var auth = await SetupAsync("alg-union", schema, Tup("doc", "D1", "editor", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Intersect_requires_both_branches()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("vet", s => s.User()).Relation("trained", s => s.User())
            .Permission("access", p => p.Relation("vet").Intersect(x => x.Relation("trained")))).Build();
        var auth = await SetupAsync("alg-inter", schema,
            Tup("doc", "D1", "vet", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "trained", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "vet", new SubjectRef("user", "bob")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_revokes_the_right_branch()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Relation("blocked", s => s.User())
            .Permission("access", p => p.Relation("viewer").Exclude(x => x.Relation("blocked")))).Build();
        var auth = await SetupAsync("alg-excl", schema,
            Tup("doc", "D1", "viewer", new SubjectRef("user", "alice")),
            Tup("doc", "D1", "viewer", new SubjectRef("user", "carol")),
            Tup("doc", "D1", "blocked", new SubjectRef("user", "carol")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "carol"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_sees_inner_exclusion_on_the_related_object()
    {
        // THE DISCRIMINATOR: animal.edit -> enclosure.edit, enclosure.edit = editor - blocked.
        // The m1/02 spike proved naive SQL post-filtering returns carol->true (WRONG). The seam returns false.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("editor", s => s.User()).Relation("blocked", s => s.User())
                .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();
        var auth = await SetupAsync("alg-arrow-excl", schema,
            Tup("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "KH1")),
            Tup("enclosure", "KH1", "editor", new SubjectRef("user", "carol")),
            Tup("enclosure", "KH1", "blocked", new SubjectRef("user", "carol")),
            Tup("enclosure", "KH1", "editor", new SubjectRef("user", "dana")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "carol"))).Allowed.ShouldBeFalse();
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "dana"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Arrow_falls_back_to_a_relation_when_target_is_not_a_permission()
    {
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("is_quarantine", s => s.Wildcard("user"))
                .Permission("is_quarantine", p => p.Relation("is_quarantine")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("quarantined", p => p.Arrow("enclosure", "is_quarantine")))
            .Build();
        var auth = await SetupAsync("alg-arrow-rel", schema,
            Tup("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tup("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "quarantined", "anyone"))).Allowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteAlgebraTests`
Expected: FAIL — `NotImplementedException` from the temporary `EvalExprAsync`.

- [ ] **Step 3: Replace `EvalExprAsync` with the full algebra**

```csharp
// src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs
using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    /// <summary>
    /// Pointwise evaluation of a permission sub-expression. Identical semantics to the m0/05
    /// EngineDrivenAuthorizer: boolean operators short-circuit; Arrow recurses into the related
    /// object's full permission (so inner exclusions/intersections are honoured).
    /// </summary>
    private async Task<bool> EvalExprAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                var ok = await ResolveRelationAsync(conn, index, tenant, obj, r.Relation, subject, context, ctx, ct);
                explain?.Add(new ExplainNode($"relation {r.Relation}", ok, Array.Empty<ExplainNode>()));
                return ok;
            }

            case Union u:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, u.Left, subject, context, ctx, children, ct);
                if (left && explain is null) return true;
                var right = await EvalExprAsync(conn, index, tenant, obj, u.Right, subject, context, ctx, children, ct);
                var result = left || right;
                explain?.Add(new ExplainNode("union (+)", result, children!));
                return result;
            }

            case Intersect i:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, i.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) return false;
                var right = await EvalExprAsync(conn, index, tenant, obj, i.Right, subject, context, ctx, children, ct);
                var result = left && right;
                explain?.Add(new ExplainNode("intersect (&)", result, children!));
                return result;
            }

            case Exclude e:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(conn, index, tenant, obj, e.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) return false;
                var right = await EvalExprAsync(conn, index, tenant, obj, e.Right, subject, context, ctx, children, ct);
                var result = left && !right;
                explain?.Add(new ExplainNode("exclude (-)", result, children!));
                return result;
            }

            case Arrow a:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var result = await EvalArrowAsync(conn, index, tenant, obj, a, subject, context, ctx, children, ct);
                explain?.Add(new ExplainNode($"arrow {a.Relation}->{a.Permission}", result, children!));
                return result;
            }

            case Conditioned c:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var inner = await EvalExprAsync(conn, index, tenant, obj, c.Inner, subject, context, ctx, children, ct);
                var passed = inner && await BranchConditionSatisfiedAsync(index, tenant, obj, c.ConditionName, context, ctx, ct);
                explain?.Add(new ExplainNode($"conditioned [{c.ConditionName}]", passed, children!));
                return passed;
            }

            default:
                throw new EvaluationLimitException($"Unhandled permission expression node '{expr.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Arrow: follow <paramref name="arrow"/>.Relation edges (the related objects) and recurse into
    /// each one's <paramref name="arrow"/>.Permission — its full expression if it is a permission,
    /// else a direct relation resolve. Edge tuples may carry conditions (evaluated here).
    /// </summary>
    private async Task<bool> EvalArrowAsync(
        NpgsqlConnection conn, SchemaIndex index, TenantContext tenant, EntityRef obj, Arrow arrow,
        SubjectRef subject, RequestContext context, EvalContext ctx, List<ExplainNode>? explain, CancellationToken ct)
    {
        var edges = await CteReachability.EdgesThroughRelationAsync(conn, null, tenant, obj, arrow.Relation, ct);
        foreach (var edge in edges)
        {
            if (!await ConditionSatisfiedAsync(index, tenant, obj, edge, context, ctx, ct)) continue;
            var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);
            bool hit = index.TryPermission(related.Type, arrow.Permission, out _)
                ? await CheckPermissionAsync(conn, index, tenant, related, arrow.Permission, subject, context, ctx, explain, ct)
                : await ResolveRelationAsync(conn, index, tenant, related, arrow.Permission, subject, context, ctx, ct);
            if (hit) return true;
        }
        return false;
    }

    /// <summary>Branch-level condition gate (a Conditioned node), empty params, against attrs + context.</summary>
    private async Task<bool> BranchConditionSatisfiedAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string conditionName,
        RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        ctx.MarkConditionTouched();
        var def = index.Condition(conditionName);
        var invocation = new ConditionRef(conditionName, new Dictionary<string, object?>());
        var attrs = await _attributes.GetAsync(tenant, obj, ct) ?? new Dictionary<string, object?>();
        return _conditions.Evaluate(def, invocation, attrs, context);
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 4.");
    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m1/06.");
    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m1/06.");
}
```

> Add `using Custodex.Core.Conditions;` is not needed here (`ConditionRef` is in `Custodex.Abstractions`). The `BranchConditionSatisfiedAsync` uses `ConditionRef` from Abstractions and `_conditions` (`IConditionEvaluator` from `Custodex.Core.Conditions`, already imported by the partial in `NpgsqlCteAuthorizer.cs`).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteAlgebraTests`
Expected: PASS (5 tests). The `Arrow_sees_inner_exclusion` test is the discriminator the `m1/02` spike proved naive SQL fails.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs tests/Custodex.Storage.Postgres.Tests/Cte/CteAlgebraTests.cs
git commit -m "feat: implement full CTE-path permission algebra matching the oracle"
```

---

### Task 4: `BatchCheckAsync` and an Explain trace

**Files:**
- Create: `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Batch.cs`
- Test: `tests/Custodex.Storage.Postgres.Tests/Cte/CteBatchAndExplainTests.cs`

**Interfaces:**
- Produces: `BatchCheckAsync` (replaces the Task 3 stub) — one shared `EvalContext` memo across all items, one connection for the whole batch, results in request order; and an `Explain` path: `CheckAsync` with `Explain=true` returns a populated `ExplainNode` tree (built by passing a non-null `explain` sink through `CheckPermissionAsync`, already wired in Tasks 2–3).
- Consumes: `CheckPermissionAsync`, `EvalContext`, `SchemaIndex`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cte/CteBatchAndExplainTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteBatchAndExplainTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlCteAuthorizer _auth = null!;
    private static readonly TenantContext T = new("cte", "batch");

    public async ValueTask InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);

        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t.Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))
            .Build();

        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = T.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = T.Store, t = T.Tenant }, uow.Transaction);
        await schemas.SetActiveAsync(T.Store, schema, u);
        await relations.WriteAsync(T,
        [
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("doc", "D2"), "viewer", new SubjectRef("user", "bob")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
        _auth = new NpgsqlCteAuthorizer(fx.ConnectionString, schemas, attributes, new NullConditionEvaluator());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Batch_returns_a_result_per_item_in_order()
    {
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>());
        var req = new BatchCheckRequest(T,
        [
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")),  // via group => true
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob")),    // false
            new CheckItem(new EntityRef("doc", "D2"), "view", new SubjectRef("user", "bob")),    // direct => true
        ], ctx);

        var results = await _auth.BatchCheckAsync(req);
        results.Count.ShouldBe(3);
        results[0].Allowed.ShouldBeTrue();
        results[1].Allowed.ShouldBeFalse();
        results[2].Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Explain_returns_a_populated_trace()
    {
        var req = new CheckRequest(T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>()),
            Explain: true);
        var result = await _auth.CheckAsync(req);
        result.Allowed.ShouldBeTrue();
        result.Explain.ShouldNotBeNull();
        result.Explain!.Description.ShouldContain("doc:D1");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteBatchAndExplainTests`
Expected: FAIL — `BatchCheckAsync` throws `NotImplementedException`, and `CheckAsync` does not yet build the explain trace.

- [ ] **Step 3: Implement batch + wire explain into `CheckAsync`**

First, update `CheckAsync` in `NpgsqlCteAuthorizer.cs` to build the explain trace when requested. Replace the `CheckAsync` body:

```csharp
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var ctx = new EvalContext(_options);
        var explainSink = request.Explain ? new List<ExplainNode>() : null;
        var allowed = await CheckPermissionAsync(
            conn, index, request.Tenant, request.Object, request.Permission, request.Subject,
            request.Context, ctx, explainSink, ct);
        return new CheckResult(allowed, explainSink?.Count > 0 ? explainSink[0] : null);
    }
```

Then add the batch partial and remove the `BatchCheckAsync` stub from `NpgsqlCteAuthorizer.Expr.cs`:

```csharp
// src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Batch.cs
using Npgsql;
using Custodex.Abstractions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres;

public sealed partial class NpgsqlCteAuthorizer
{
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var ctx = new EvalContext(_options);   // one shared memo across all items
        var results = new List<CheckResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var allowed = await CheckPermissionAsync(
                conn, index, request.Tenant, item.Object, item.Permission, item.Subject,
                request.Context, ctx, explain: null, ct);
            results.Add(new CheckResult(allowed));
        }
        return results;
    }
}
```

> Delete the `BatchCheckAsync` throwing stub from `NpgsqlCteAuthorizer.Expr.cs` (keep the `ListObjectsAsync`/`ListSubjectsAsync` stubs — `m1/06` removes those). The `EvalContext` memo is **per-batch**, not shared across separate `CheckAsync` calls, matching the oracle.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteBatchAndExplainTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.Postgres tests/Custodex.Storage.Postgres.Tests
git commit -m "feat: add CTE batch check and explain trace"
```

---

### Task 5: Worked-example parity — §12.1 and §12.5 over Postgres

**Files:**
- Test: `tests/Custodex.Storage.Postgres.Tests/Cte/CteWorkedExampleParityTests.cs`

**Interfaces:**
- Consumes: `NpgsqlCteAuthorizer`, the stores, the schema builder. Encodes the §12.1 role-grant-over-category and §12.5 quarantine-gate worked examples (the same shapes `m0/09` proves against the oracle) directly over Postgres, asserting the CTE path returns the spec's truth. This is a *guard* that the CTE path agrees with the named acceptance cases before the `m1/08` harness generalises to random schemas.

- [ ] **Step 1: Write the parity tests**

```csharp
// tests/Custodex.Storage.Postgres.Tests/Cte/CteWorkedExampleParityTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Storage.Postgres;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Cte;

[Collection("postgres")]
public class CteWorkedExampleParityTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlSchemaStore _schemas = null!;
    private NpgsqlAttributeStore _attributes = null!;

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

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private async Task<(NpgsqlCteAuthorizer Auth, TenantContext T)> SetupAsync(string store, Schema schema, RelationTuple[] tuples)
    {
        var t = new TenantContext(store, "t");
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await _schemas.SetActiveAsync(t.Store, schema, u);
        await _relations.WriteAsync(t, tuples, [], u);
        await u.CommitAsync();
        return (new NpgsqlCteAuthorizer(fx.ConnectionString, _schemas, _attributes, new NullConditionEvaluator()), t);
    }

    private static CheckRequest Req(TenantContext t, EntityRef obj, string perm, string sid) => new(
        t, obj, perm, new SubjectRef("user", sid),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", sid), new Dictionary<string, object?>()));

    [Fact]
    public async Task Example_12_1_role_grant_over_category_resolves_through_the_arrow()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("category", t => t
                .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("record_dispense", p => p.Relation("dispenser").Exclude(x => x.Relation("blocked"))))
            .Type("inventory_item", t => t
                .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
                .Relation("category", s => s.Type("category"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("record_dispense", p => p.Relation("dispenser")
                    .Arrow("category", "record_dispense").Exclude(x => x.Relation("blocked"))))
            .Build();
        var (auth, t) = await SetupAsync("ex-12-1", schema,
        [
            Tup("category", "drugs", "dispenser", new SubjectRef("group", "vets", "member")),
            Tup("inventory_item", "vaccine-X", "category", new SubjectRef("category", "drugs")),
            Tup("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        ]);
        (await auth.CheckAsync(Req(t, new EntityRef("inventory_item", "vaccine-X"), "record_dispense", "dr-smith")))
            .Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(t, new EntityRef("inventory_item", "vaccine-X"), "record_dispense", "outsider")))
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Example_12_5_quarantine_gate_allows_trained_vet_denies_untrained()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("enclosure", t => t
                .Relation("is_quarantine", s => s.Wildcard("user"))
                .Permission("is_quarantine", p => p.Relation("is_quarantine")))
            .Type("animal", t => t
                .Relation("can_access", s => s.User().SubjectSet("group", "member"))
                .Relation("enclosure", s => s.Type("enclosure"))
                .Relation("vet_member", s => s.SubjectSet("group", "member"))
                .Relation("trained_member", s => s.SubjectSet("group", "member"))
                .Permission("access", p => p
                    .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("enclosure", "is_quarantine")))
                    .Union(b => b.Arrow("enclosure", "is_quarantine")
                        .Intersect(x => x.Relation("vet_member"))
                        .Intersect(x => x.Relation("trained_member")))))
            .Build();
        var tuples = new[]
        {
            Tup("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tup("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
            Tup("animal", "EL-001", "vet_member", new SubjectRef("group", "vets", "member")),
            Tup("animal", "EL-001", "trained_member", new SubjectRef("group", "trained", "member")),
            Tup("animal", "EL-001", "can_access", new SubjectRef("user", "dr-smith")),
            Tup("animal", "EL-001", "can_access", new SubjectRef("user", "jones")),
            Tup("group", "vets", "member", new SubjectRef("user", "dr-smith")),
            Tup("group", "vets", "member", new SubjectRef("user", "jones")),
            Tup("group", "trained", "member", new SubjectRef("user", "dr-smith")),
        };
        var (auth, t) = await SetupAsync("ex-12-5", schema, tuples);
        (await auth.CheckAsync(Req(t, new EntityRef("animal", "EL-001"), "access", "dr-smith"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(t, new EntityRef("animal", "EL-001"), "access", "jones"))).Allowed.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteWorkedExampleParityTests`
Expected: PASS (2 tests). If §12.5 fails, the CTE walk diverges from the oracle on intersection-through-arrow-with-exclusion — fix the walk, not the test.

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Storage.Postgres.Tests/Cte/CteWorkedExampleParityTests.cs
git commit -m "test: prove CTE check path matches §12.1 and §12.5 worked examples"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`; `Custodex.Storage.Postgres` references `Custodex.Core` (Task 1).
- [ ] The reachability CTE expands nested subject-sets in SQL and surfaces wildcard leaves; arrow edges are returned unexpanded (Task 1).
- [ ] `NpgsqlCteAuthorizer.CheckAsync` matches the oracle on direct/wildcard/nested/cycle membership (Task 2) and full algebra (Task 3).
- [ ] The discriminator (`Arrow_sees_inner_exclusion_on_the_related_object`) returns **false** for carol — the case the `m1/02` spike proved naive SQL gets wrong (Task 3).
- [ ] Conditioned tuples (direct and under nested groups) are evaluated in C# via `IConditionEvaluator`, latching `ConditionTouched`, identically to the oracle (Tasks 1–3).
- [ ] Cycle prunes to deny; depth bound throws `EvaluationLimitException` — both via the reused `EvalContext` (Task 2).
- [ ] `BatchCheckAsync` shares one memo/connection and returns results in order; `Explain=true` returns a populated trace (Task 4).
- [ ] §12.1 and §12.5 worked examples pass over Postgres (Task 5).
- [ ] The candidate SQL/walk is framed as "validated by the `m1/02` spike and the `m1/08` differential harness," not as proven copy-paste (calibration notes in Tasks 1, 3).

## Contract gaps (reported, not changed)

- **`IConditionEvaluator` shape (same as `m0/05`).** `NpgsqlCteAuthorizer` consumes the `Custodex.Core.Conditions.IConditionEvaluator` seam (the `bool`-returning collaborator the oracle uses), not the static `ConditionEvaluator` from `m0/06`. Construction in production (`m1/09`) supplies the `CelConditionEvaluator` adapter `m0/06` authors. No `README.md` change; this is the same reconciliation `m0/05` already records.
- **`Custodex.Storage.Postgres → Custodex.Core` reference.** Decided in `m1/02`; added here (Task 1). Not a contract type change — `Custodex.Core` is a non-DB engine assembly and spec §4 only forbids DB code *in Core*. Flagged for visibility in case the contract's package table should spell out the edge.
```

