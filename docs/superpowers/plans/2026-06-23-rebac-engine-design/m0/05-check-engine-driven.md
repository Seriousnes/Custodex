# M0/05 — Engine-Driven Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement engine-driven `CheckAsync` in `Custodex.Core` (`EngineDrivenAuthorizer : IAuthorizer`) that walks the `PermExpr` of an object's permission as a **pointwise recursive membership test** for the query subject, with cycle/depth guards, per-request memoization, `Explain` trees, and `Custodex.Diagnostics` telemetry.

**Architecture:** Check is **not** set materialization. It asks "is *this* subject a member of the permission's resolved set?" and recurses: `RelationRef` matches a tuple's subject directly, by wildcard `type:*`, or by expanding a subject-set `group:G#rel` (recurse into `G`'s relation); `Union`/`Intersect`/`Exclude` short-circuit boolean composition over sub-results; `Arrow(rel,perm)` resolves related objects via `rel` and recurses into *their* full permission expression. Because Arrow recurses into the related object's whole expression, an inner `- blocked` is always seen — there is no top-level post-filter, which is exactly why this path is the correctness oracle. This plan implements Check only; `ListObjects`/`ListSubjects`/`BatchCheck` extend `EngineDrivenAuthorizer` in `m0/07`. Conditioned branches are wired in `m0/06`; here a hook is left and conditions are treated as transparently true until that plan lands.

**Tech Stack:** .NET 10, C# 14, xUnit, Shouldly. Uses the `Custodex.Storage.InMemory` provider (built in `m0/04`) in tests.

## Global Constraints

See `../README.md` → Global Constraints. Key points: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation (ambient time enters only via `RequestContext.Now`). Depends on `m0/01` (Abstractions + diagnostics), `m0/02` (schema builder), `m0/03` (validation), `m0/04` (in-memory stores).

**Cycle vs depth — the two guards are distinct (spec §10.3 vs contract `EvaluationLimitException`):**
- A **cycle** (the same `(object, permission, subject)` frame re-entered on the current DFS path, e.g. a nested-group loop) is normal data. It is **pruned and contributes `false`** — never an exception. A membership question that depends on itself cannot add a new grant.
- A **depth bound** exceeded (configurable, default 64 nested frames) throws `EvaluationLimitException`. This is the "guard tripped → deny + diagnostic" of §10.3, surfaced as the typed exception the contract defines.

These reconcile the spec's prose ("depth/cycle guard tripped → deny") with the contract's `EvaluationLimitException`: cycles prune to deny silently; the depth ceiling is the hard limit that throws.

> **Condition-evaluator seam (`IConditionEvaluator`).** This plan defines a small `Custodex.Core` seam the authorizer depends on (Task 2):
> ```csharp
> // Custodex.Core.Conditions — the seam the authorizer consumes
> public interface IConditionEvaluator
> {
>     bool Evaluate(ConditionDef definition, ConditionRef invocation,
>         IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext context);
> }
> ```
> The real condition engine is `m0/06`'s `ConditionEvaluator` — a **static class** with a different shape:
> `ConditionResult Evaluate(ConditionDef definition, IReadOnlyDictionary<string,object?> attributes, RequestContext context, IReadOnlyDictionary<string,object?> parameters)`.
> The two do not match by accident: the authorizer needs an injectable `bool`-returning collaborator (so `NullConditionEvaluator` works in pure-ReBAC tests and the cache's condition-touched logic is uniform), while `m0/06` ships a static, `ConditionResult`-returning evaluator. They are reconciled by a thin **adapter** (`CelConditionEvaluator : IConditionEvaluator`) authored in `m0/06` that forwards to the static `ConditionEvaluator.Evaluate`, maps `ConditionRef.Parameters → parameters`, and maps `ConditionResult.Allow → true` (Deny/Error → false, honouring spec §10.3 default-deny). Until `m0/06` lands, `EngineDrivenAuthorizer` is constructed with the `NullConditionEvaluator` (Task 2), and the "a condition was touched" flag (Task 7) is still recorded so the `m0/08` cache never caches a condition-dependent result. See the Contract gaps note at the foot of this plan for the exact mismatch.

---

### Task 1: Evaluation context — memo, visited set, depth budget

**Files:**
- Create: `src/Custodex.Core/Evaluation/EvalContext.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/EvalContextTests.cs`

**Interfaces:**
- Produces: `EvalContext` carrying the per-request memo (`(object,permission,subject) → bool`), the current-path visited set (cycle guard), a depth counter against a configurable bound, and a "condition touched" flag. `EvalFrame` readonly struct as the memo/visited key. `EvaluationOptions(int MaxDepth = 64)`.
- Consumes: `EntityRef`, `SubjectRef` from `Custodex.Abstractions`; `EvaluationLimitException`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/EvalContextTests.cs
using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class EvalContextTests
{
    private static EvalFrame Frame(string perm = "view") =>
        new(new EntityRef("animal", "EL-001"), perm, new SubjectRef("user", "alice"));

    [Fact]
    public void Memo_stores_and_returns_completed_results()
    {
        var ctx = new EvalContext(new EvaluationOptions());
        ctx.TryGetMemo(Frame(), out _).ShouldBeFalse();
        ctx.SetMemo(Frame(), true);
        ctx.TryGetMemo(Frame(), out var hit).ShouldBeTrue();
        hit.ShouldBeTrue();
    }

    [Fact]
    public void Entering_same_frame_twice_on_path_is_detected_as_cycle()
    {
        var ctx = new EvalContext(new EvaluationOptions());
        ctx.TryEnter(Frame(), out var scope).ShouldBeTrue();
        ctx.TryEnter(Frame(), out _).ShouldBeFalse();   // cycle: already on path
        scope.Dispose();
        ctx.TryEnter(Frame(), out _).ShouldBeTrue();     // left path -> enterable again
    }

    [Fact]
    public void Exceeding_depth_bound_throws_evaluation_limit()
    {
        var ctx = new EvalContext(new EvaluationOptions(MaxDepth: 2));
        ctx.TryEnter(Frame("a"), out _).ShouldBeTrue();
        ctx.TryEnter(Frame("b"), out _).ShouldBeTrue();
        Should.Throw<EvaluationLimitException>(() => ctx.TryEnter(Frame("c"), out _));
    }

    [Fact]
    public void Condition_touched_flag_latches()
    {
        var ctx = new EvalContext(new EvaluationOptions());
        ctx.ConditionTouched.ShouldBeFalse();
        ctx.MarkConditionTouched();
        ctx.ConditionTouched.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter EvalContextTests`
Expected: FAIL — `EvalContext` not defined.

- [ ] **Step 3: Implement the evaluation context**

```csharp
// src/Custodex.Core/Evaluation/EvalContext.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed record EvaluationOptions(int MaxDepth = 64);

public readonly record struct EvalFrame(EntityRef Object, string Permission, SubjectRef Subject);

/// <summary>
/// Per-request evaluation state: a memo of completed sub-checks, a visited set
/// guarding the current DFS path against cycles, a depth budget, and a latch
/// recording whether any condition was reached (so the caching layer in m0/08
/// never caches a condition-dependent decision).
/// </summary>
public sealed class EvalContext
{
    private readonly EvaluationOptions _options;
    private readonly Dictionary<EvalFrame, bool> _memo = new();
    private readonly HashSet<EvalFrame> _onPath = new();
    private int _depth;

    public EvalContext(EvaluationOptions options) => _options = options;

    public bool ConditionTouched { get; private set; }
    public void MarkConditionTouched() => ConditionTouched = true;

    public bool TryGetMemo(EvalFrame frame, out bool result) => _memo.TryGetValue(frame, out result);
    public void SetMemo(EvalFrame frame, bool result) => _memo[frame] = result;

    /// <summary>
    /// Enters <paramref name="frame"/> on the current path. Returns false (without
    /// entering) if the frame is already on the path — a cycle, which the caller
    /// treats as a non-contributing <c>false</c>. Throws when the depth bound is
    /// exceeded. Dispose the returned scope to leave the frame.
    /// </summary>
    public bool TryEnter(EvalFrame frame, out PathScope scope)
    {
        if (_onPath.Contains(frame))
        {
            scope = PathScope.NoOp;
            return false;
        }
        if (_depth >= _options.MaxDepth)
            throw new EvaluationLimitException(
                $"Evaluation depth bound of {_options.MaxDepth} exceeded at {frame.Object}#{frame.Permission}@{frame.Subject}.");

        _onPath.Add(frame);
        _depth++;
        scope = new PathScope(this, frame);
        return true;
    }

    private void Leave(EvalFrame frame)
    {
        _onPath.Remove(frame);
        _depth--;
    }

    public readonly struct PathScope : IDisposable
    {
        private readonly EvalContext? _ctx;
        private readonly EvalFrame _frame;
        internal PathScope(EvalContext ctx, EvalFrame frame) { _ctx = ctx; _frame = frame; }
        public static PathScope NoOp => default;
        public void Dispose() => _ctx?.Leave(_frame);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter EvalContextTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/EvalContext.cs tests/Custodex.Core.Tests/Evaluation/EvalContextTests.cs
git commit -m "feat: add evaluation context with memo, cycle and depth guards"
```

---

### Task 2: Condition-evaluator seam (null implementation until m0/06)

**Files:**
- Create: `src/Custodex.Core/Conditions/IConditionEvaluator.cs`
- Create: `src/Custodex.Core/Conditions/NullConditionEvaluator.cs`
- Test: `tests/Custodex.Core.Tests/Conditions/NullConditionEvaluatorTests.cs`

**Interfaces:**
- Produces: `IConditionEvaluator` (the seam `m0/06` will implement for real) and `NullConditionEvaluator` (always satisfied), so this plan can construct `EngineDrivenAuthorizer` before the real evaluator exists.
- Consumes: `ConditionDef`, `ConditionRef`, `RequestContext`.

> When `m0/06` lands, it provides the real `ConditionEvaluator : IConditionEvaluator`; `NullConditionEvaluator` remains for tests that exercise pure ReBAC without conditions. The interface signature here is the contract `m0/06` must honour.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/Conditions/NullConditionEvaluatorTests.cs
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class NullConditionEvaluatorTests
{
    [Fact]
    public void Null_evaluator_treats_every_condition_as_satisfied()
    {
        var eval = new NullConditionEvaluator();
        var def = new ConditionDef("within_hours",
            new[] { new ConditionParam("start", ConditionType.Int) },
            new TrueBody());
        var inv = new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8 });
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"),
            new Dictionary<string, object?>());

        eval.Evaluate(def, inv, new Dictionary<string, object?>(), ctx).ShouldBeTrue();
    }

    private sealed record TrueBody : ConditionExpr;
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter NullConditionEvaluatorTests`
Expected: FAIL — `IConditionEvaluator` / `NullConditionEvaluator` not defined.

- [ ] **Step 3: Implement the seam**

```csharp
// src/Custodex.Core/Conditions/IConditionEvaluator.cs
using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// Evaluates a schema-declared condition against synced resource attributes,
/// request context, and the tuple's stored parameters. The real implementation
/// (<c>ConditionEvaluator</c>) is built in m0/06; this signature is its contract.
/// </summary>
public interface IConditionEvaluator
{
    bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context);
}
```

```csharp
// src/Custodex.Core/Conditions/NullConditionEvaluator.cs
using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>Treats every condition as satisfied. Used before m0/06 and in pure-ReBAC tests.</summary>
public sealed class NullConditionEvaluator : IConditionEvaluator
{
    public bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context) => true;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter NullConditionEvaluatorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Conditions tests/Custodex.Core.Tests/Conditions
git commit -m "feat: add condition-evaluator seam and null implementation"
```

---

### Task 3: Schema lookup helpers

**Files:**
- Create: `src/Custodex.Core/Evaluation/SchemaIndex.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/SchemaIndexTests.cs`

**Interfaces:**
- Produces: `SchemaIndex` wrapping a `Schema` with O(1) lookups: `EntityTypeDef Type(string name)` (throws `UnknownTypeException`), `PermissionDef Permission(string type, string perm)` (throws `UnknownPermissionException`), `RelationDef Relation(string type, string rel)` (throws `UnknownRelationException`), `ConditionDef Condition(string name)`, and `bool TryPermission(string type, string perm, out PermissionDef def)`.
- Consumes: `Schema`, `EntityTypeDef`, `PermissionDef`, `RelationDef`, `ConditionDef`, and the `Unknown*Exception` types.

> Arrow traversal needs to ask "does the related type have a permission named X, or only a relation named X?" — `record_dispense` on a category is a permission, but `enclosure` on an animal is a relation. `TryPermission` lets the walker fall back from permission to relation. Lookups are ordinal-string keyed.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/SchemaIndexTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Evaluation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class SchemaIndexTests
{
    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("animal", t => t
            .Relation("medicator", s => s.User().SubjectSet("group", "member"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Permission("edit", p => p.Relation("medicator")))
        .Build();

    [Fact]
    public void Resolves_known_type_relation_and_permission()
    {
        var idx = new SchemaIndex(Build());
        idx.Type("animal").Name.ShouldBe("animal");
        idx.Relation("animal", "enclosure").Name.ShouldBe("enclosure");
        idx.Permission("animal", "edit").Name.ShouldBe("edit");
    }

    [Fact]
    public void Unknown_lookups_throw_typed_exceptions()
    {
        var idx = new SchemaIndex(Build());
        Should.Throw<UnknownTypeException>(() => idx.Type("dragon"));
        Should.Throw<UnknownRelationException>(() => idx.Relation("animal", "nope"));
        Should.Throw<UnknownPermissionException>(() => idx.Permission("animal", "nope"));
    }

    [Fact]
    public void TryPermission_distinguishes_permission_from_relation()
    {
        var idx = new SchemaIndex(Build());
        idx.TryPermission("animal", "edit", out _).ShouldBeTrue();
        idx.TryPermission("animal", "enclosure", out _).ShouldBeFalse();   // a relation, not a permission
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter SchemaIndexTests`
Expected: FAIL — `SchemaIndex` not defined.

- [ ] **Step 3: Implement the index**

```csharp
// src/Custodex.Core/Evaluation/SchemaIndex.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed class SchemaIndex
{
    private readonly Dictionary<string, EntityTypeDef> _types;
    private readonly Dictionary<(string Type, string Name), PermissionDef> _permissions;
    private readonly Dictionary<(string Type, string Name), RelationDef> _relations;
    private readonly Dictionary<string, ConditionDef> _conditions;

    public SchemaIndex(Schema schema)
    {
        Schema = schema;
        _types = schema.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _permissions = new();
        _relations = new();
        foreach (var t in schema.Types)
        {
            foreach (var p in t.Permissions) _permissions[(t.Name, p.Name)] = p;
            foreach (var r in t.Relations) _relations[(t.Name, r.Name)] = r;
        }
        _conditions = schema.Conditions.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public Schema Schema { get; }

    public EntityTypeDef Type(string name) =>
        _types.TryGetValue(name, out var t) ? t : throw new UnknownTypeException(name);

    public PermissionDef Permission(string type, string perm)
    {
        if (!_types.ContainsKey(type)) throw new UnknownTypeException(type);
        return _permissions.TryGetValue((type, perm), out var p)
            ? p : throw new UnknownPermissionException(type, perm);
    }

    public bool TryPermission(string type, string perm, out PermissionDef def) =>
        _permissions.TryGetValue((type, perm), out def!);

    public RelationDef Relation(string type, string rel)
    {
        if (!_types.ContainsKey(type)) throw new UnknownTypeException(type);
        return _relations.TryGetValue((type, rel), out var r)
            ? r : throw new UnknownRelationException(type, rel);
    }

    public bool TryRelation(string type, string rel, out RelationDef def) =>
        _relations.TryGetValue((type, rel), out def!);

    public ConditionDef Condition(string name) =>
        _conditions.TryGetValue(name, out var c)
            ? c : throw new UnknownPermissionException("condition", name);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter SchemaIndexTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/SchemaIndex.cs tests/Custodex.Core.Tests/Evaluation/SchemaIndexTests.cs
git commit -m "feat: add schema lookup index for evaluation"
```

---

### Task 4: Subject-set membership — direct, wildcard, nested groups

**Files:**
- Create: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/SubjectMembershipTests.cs`

**Interfaces:**
- Produces: `EngineDrivenAuthorizer` (constructor only + a private `ResolveRelationAsync`). Constructor: `EngineDrivenAuthorizer(ISchemaStore schema, IRelationStore relations, IAttributeStore attributes, IConditionEvaluator conditions, EvaluationOptions? options = null)`. This task adds the **relation-membership** primitive used by every later operator: does `subject` fill `object#relation` directly, via wildcard `type:*`, or via a nested `group:G#rel` subject-set?
- Consumes: `ISchemaStore`, `IRelationStore`, `IAttributeStore` from `Custodex.Abstractions` (`m0/04` in-memory impls in tests); `EvalContext`, `SchemaIndex`, `IConditionEvaluator`.

> **Subject-set expansion is the heart of nesting.** A tuple `object#relation@group:G#member` does not directly name a user; it delegates to "whoever is in `G#member`". To test whether `subject` matches, the walker recurses: fetch `G#member` tuples and ask the same membership question one level down. Wildcard `type:*` matches any subject of that type unconditionally. A tuple's own `Condition` (if present) is evaluated through `IConditionEvaluator`, and reaching one latches `ConditionTouched`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/SubjectMembershipTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class SubjectMembershipTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema GroupSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Permission("view", p => p.Relation("viewer")))
        .Build();

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Length > 0) await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest Req(string subjectId, string perm = "view") => new(
        T, new EntityRef("doc", "D1"), perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId),
            new Dictionary<string, object?>()));

    [Fact]
    public async Task Direct_user_grant_matches()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req("alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Wildcard_grant_matches_everyone()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req("anyone"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Subject_set_grant_matches_via_group_membership()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "dr-smith")));
        (await auth.CheckAsync(Req("dr-smith"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("outsider"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Nested_group_membership_resolves_transitively()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "staff", "member")),
            new RelationTuple(new EntityRef("group", "staff"), "member", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "dr-smith")));
        (await auth.CheckAsync(Req("dr-smith"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Group_membership_cycle_prunes_to_deny_without_throwing()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "a", "member")),
            new RelationTuple(new EntityRef("group", "a"), "member", new SubjectRef("group", "b", "member")),
            new RelationTuple(new EntityRef("group", "b"), "member", new SubjectRef("group", "a", "member")));
        (await auth.CheckAsync(Req("ghost"))).Allowed.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter SubjectMembershipTests`
Expected: FAIL — `EngineDrivenAuthorizer` not defined (and `CheckAsync` not yet implemented).

- [ ] **Step 3: Implement the authorizer skeleton + membership primitive + a minimal `CheckAsync` for `RelationRef`**

This step lands the constructor, the relation-membership resolver, and just enough `CheckAsync` to evaluate a bare `RelationRef` permission (Task 5 generalizes `CheckAsync` to the full algebra). `ListObjects`/`ListSubjects`/`BatchCheck` throw `NotImplementedException` here and are implemented in `m0/07`.

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs
using System.Diagnostics;
using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer : IAuthorizer
{
    private readonly ISchemaStore _schemaStore;
    private readonly IRelationStore _relations;
    private readonly IAttributeStore _attributes;
    private readonly IConditionEvaluator _conditions;
    private readonly EvaluationOptions _options;

    public EngineDrivenAuthorizer(
        ISchemaStore schemaStore,
        IRelationStore relations,
        IAttributeStore attributes,
        IConditionEvaluator conditions,
        EvaluationOptions? options = null)
    {
        _schemaStore = schemaStore;
        _relations = relations;
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
        var ctx = new EvalContext(_options);
        var allowed = await CheckPermissionAsync(
            index, request.Tenant, request.Object, request.Permission, request.Subject,
            request.Context, ctx, explain: null, ct);
        return new CheckResult(allowed);
    }

    /// <summary>
    /// Pointwise membership: does <paramref name="subject"/> hold
    /// <paramref name="permission"/> on <paramref name="obj"/>? Evaluates the
    /// permission's <see cref="PermExpr"/> recursively. The optional
    /// <paramref name="explain"/> sink collects a trace node per visited branch.
    /// </summary>
    private async Task<bool> CheckPermissionAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string permission,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
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
            var result = await EvalExprAsync(index, tenant, obj, def.Expression, subject, context, ctx, children, ct);
            explain?.Add(new ExplainNode($"{obj}#{permission}", result, children!));
            if (explain is null) ctx.SetMemo(frame, result);
            return result;
        }
    }

    /// <summary>Evaluates whether <paramref name="subject"/> fills <paramref name="obj"/>#<paramref name="relation"/>.</summary>
    private async Task<bool> ResolveRelationAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, string relation,
        SubjectRef subject, RequestContext context, EvalContext ctx, CancellationToken ct)
    {
        var tuples = await _relations.GetByObjectAsync(tenant, obj, relation, ct);
        foreach (var tuple in tuples)
        {
            if (!await ConditionSatisfiedAsync(index, tenant, obj, tuple, context, ctx, ct))
                continue;

            var s = tuple.Subject;

            // Wildcard: type:* grants every subject of that type.
            if (s.IsWildcard && string.Equals(s.Type, subject.Type, StringComparison.Ordinal))
                return true;

            // Direct subject match.
            if (!s.IsSubjectSet && !s.IsWildcard
                && string.Equals(s.Type, subject.Type, StringComparison.Ordinal)
                && string.Equals(s.Id, subject.Id, StringComparison.Ordinal))
                return true;

            // Subject-set: group:G#rel — recurse into G's relation.
            if (s.IsSubjectSet)
            {
                var nestedObj = new EntityRef(s.Type, s.Id);
                if (await ResolveRelationAsync(index, tenant, nestedObj, s.Relation!, subject, context, ctx, ct))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Evaluates a tuple's carried condition. A tuple with no condition is always
    /// satisfied. Reaching a condition latches <see cref="EvalContext.ConditionTouched"/>
    /// so the m0/08 cache never caches this decision.
    /// </summary>
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

> `EvalExprAsync` is implemented in Task 5. For Task 4 to compile and exercise only `RelationRef`, add a temporary minimal `EvalExprAsync` handling `RelationRef` and throwing for the rest. Task 5 replaces it.

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Expr.cs  (temporary minimal form; Task 5 replaces)
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    private async Task<bool> EvalExprAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                var ok = await ResolveRelationAsync(index, tenant, obj, r.Relation, subject, context, ctx, ct);
                explain?.Add(new ExplainNode($"relation {r.Relation}", ok, Array.Empty<ExplainNode>()));
                return ok;
            }
            default:
                throw new NotImplementedException("Full algebra is implemented in Task 5.");
        }
    }
}
```

Add the not-yet-implemented `IAuthorizer` members so the class satisfies the interface; `m0/07` fills them in:

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.List.cs  (stubs; m0/07 implements)
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m0/07.");

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m0/07.");

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m0/07.");
}
```

Add the InMemory provider reference to the test project if not already present:

```bash
dotnet add tests/Custodex.Core.Tests reference src/Custodex.Storage.InMemory
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter SubjectMembershipTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation tests/Custodex.Core.Tests/Evaluation/SubjectMembershipTests.cs
git commit -m "feat: add engine-driven authorizer with subject-set membership"
```

---

### Task 5: Full algebra — Union, Intersect, Exclude, Arrow, Conditioned

**Files:**
- Modify: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Expr.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/AlgebraTests.cs`

**Interfaces:**
- Produces: the complete `EvalExprAsync` handling all six `PermExpr` node kinds with short-circuiting and arrow recursion.
- Consumes: `ResolveRelationAsync`, `CheckPermissionAsync` (Task 4); `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned`, `RelationRef` from `Custodex.Abstractions`.

> **Calibration note.** This is the algorithm-heavy heart of the engine and the genuinely hard part. The code below is the *approach to validate*, not guaranteed-correct copy-paste: nested intersection/exclusion interleaved with arrow traversal is exactly where a single code block can hide a bug. The durable correctness mechanism is `m0/09`'s six worked examples and CsCheck invariants, plus the M1 differential harness. Treat the tests in this task and in `m0/09` as the specification; the code is the candidate.
>
> **Operator semantics (pointwise):**
> - `Union(L,R)` = `S∈L OR S∈R` — short-circuit on the first true.
> - `Intersect(L,R)` = `S∈L AND S∈R` — short-circuit on the first false.
> - `Exclude(L,R)` = `S∈L AND NOT S∈R` — evaluate L first; if false, skip R.
> - `Arrow(rel,perm)` = there exists a related object `O'` via `rel` such that `S` holds `perm` on `O'`. Recurse into `O'`'s **full** permission expression (this is why the engine sees inner exclusions and is the oracle). If the related type has no permission named `perm` but does have a relation named `perm`, fall back to a relation resolve on `O'` (arrows may target a relation, e.g. `enclosure->is_quarantine` where `is_quarantine` is the relation that backs the same-named permission).
> - `Conditioned(Inner, name)` — the branch contributes only when both `Inner` holds and the named condition is satisfied. The condition here is **not** tuple-carried; it is a branch-level gate keyed by `name` against an invocation with empty parameters, evaluated against request context + resource attributes. Reaching it latches `ConditionTouched`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Evaluation/AlgebraTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class AlgebraTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Length > 0) await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest Req(EntityRef obj, string perm, string subjectId) => new(
        T, obj, perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId),
            new Dictionary<string, object?>()));

    private static RelationTuple Tuple(string objType, string objId, string rel, SubjectRef subject) =>
        new(new EntityRef(objType, objId), rel, subject);

    [Fact]
    public async Task Union_grants_if_either_branch_holds()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("editor", s => s.User())
                .Permission("access", p => p.Relation("viewer").Union(x => x.Relation("editor"))))
            .Build();
        var auth = await NewAsync(schema, Tuple("doc", "D1", "editor", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Intersect_requires_both_branches()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("vet", s => s.User())
                .Relation("trained", s => s.User())
                .Permission("access", p => p.Relation("vet").Intersect(x => x.Relation("trained"))))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("doc", "D1", "vet", new SubjectRef("user", "alice")),
            Tuple("doc", "D1", "trained", new SubjectRef("user", "alice")),
            Tuple("doc", "D1", "vet", new SubjectRef("user", "bob")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_revokes_the_right_branch()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("access", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "alice")),
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "carol")),
            Tuple("doc", "D1", "blocked", new SubjectRef("user", "carol")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "carol"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_self_is_always_deny()
    {
        // a - a == deny for everyone.
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("access", p => p.Relation("viewer").Exclude(x => x.Relation("viewer"))))
            .Build();
        var auth = await NewAsync(schema, Tuple("doc", "D1", "viewer", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_inherits_through_a_related_object_permission()
    {
        // animal.edit = enclosure->edit ; enclosure.edit = editor
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("editor", s => s.User())
                .Permission("edit", p => p.Relation("editor")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "KH1")),
            Tuple("enclosure", "KH1", "editor", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_sees_inner_exclusion_on_the_related_object()
    {
        // The landmine: animal.edit -> enclosure.edit, and enclosure.edit contains - blocked.
        // A top-level post-filter could not see the inner exclusion; pointwise arrow recursion does.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("editor", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "KH1")),
            Tuple("enclosure", "KH1", "editor", new SubjectRef("user", "carol")),
            Tuple("enclosure", "KH1", "blocked", new SubjectRef("user", "carol")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "carol"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_falls_back_to_a_relation_when_target_is_not_a_permission()
    {
        // enclosure->is_quarantine where is_quarantine is a relation backing the gate.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("is_quarantine", s => s.Wildcard("user"))
                .Permission("is_quarantine", p => p.Relation("is_quarantine")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("quarantined", p => p.Arrow("enclosure", "is_quarantine")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "quarantined", "anyone"))).Allowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter AlgebraTests`
Expected: FAIL — `NotImplementedException` from the temporary `EvalExprAsync`.

- [ ] **Step 3: Replace `EvalExprAsync` with the full algebra**

```csharp
// src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Expr.cs
using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    /// <summary>
    /// Pointwise evaluation of a permission sub-expression for a single subject.
    /// Returns true iff the subject is a member of the set the expression denotes
    /// on <paramref name="obj"/>. Boolean operators short-circuit; Arrow recurses
    /// into the related object's own permission expression (so inner exclusions and
    /// intersections are always honoured).
    /// </summary>
    private async Task<bool> EvalExprAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, PermExpr expr,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        switch (expr)
        {
            case RelationRef r:
            {
                var ok = await ResolveRelationAsync(index, tenant, obj, r.Relation, subject, context, ctx, ct);
                explain?.Add(new ExplainNode($"relation {r.Relation}", ok, Array.Empty<ExplainNode>()));
                return ok;
            }

            case Union u:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(index, tenant, obj, u.Left, subject, context, ctx, children, ct);
                if (left && explain is null) return true;   // short-circuit when not explaining
                // When explaining, always evaluate the right branch so the trace records both.
                var right = await EvalExprAsync(index, tenant, obj, u.Right, subject, context, ctx, children, ct);
                var result = left || right;
                explain?.Add(new ExplainNode("union (+)", result, children!));
                return result;
            }

            case Intersect i:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(index, tenant, obj, i.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) { return false; }   // short-circuit when not explaining
                var right = await EvalExprAsync(index, tenant, obj, i.Right, subject, context, ctx, children, ct);
                var result = left && right;
                explain?.Add(new ExplainNode("intersect (&)", result, children!));
                return result;
            }

            case Exclude e:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var left = await EvalExprAsync(index, tenant, obj, e.Left, subject, context, ctx, children, ct);
                if (!left && explain is null) { return false; }   // short-circuit when not explaining
                var right = await EvalExprAsync(index, tenant, obj, e.Right, subject, context, ctx, children, ct);
                var result = left && !right;
                explain?.Add(new ExplainNode("exclude (-)", result, children!));
                return result;
            }

            case Arrow a:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var result = await EvalArrowAsync(index, tenant, obj, a, subject, context, ctx, children, ct);
                explain?.Add(new ExplainNode($"arrow {a.Relation}->{a.Permission}", result, children!));
                return result;
            }

            case Conditioned c:
            {
                var children = explain is null ? null : new List<ExplainNode>();
                var inner = await EvalExprAsync(index, tenant, obj, c.Inner, subject, context, ctx, children, ct);
                var passed = inner && await BranchConditionSatisfiedAsync(index, tenant, obj, c.ConditionName, context, ctx, ct);
                explain?.Add(new ExplainNode($"conditioned [{c.ConditionName}]", passed, children!));
                return passed;
            }

            default:
                throw new EvaluationLimitException($"Unhandled permission expression node '{expr.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Arrow: gather the objects related to <paramref name="obj"/> through
    /// <paramref name="arrow"/>.Relation and recurse into each one's
    /// <paramref name="arrow"/>.Permission. A related object's subject in the
    /// relation tuple is the target entity (e.g. <c>enclosure:KH1</c>). If the
    /// target type defines a permission of that name we evaluate the full
    /// permission; otherwise we fall back to a direct relation resolve.
    /// </summary>
    private async Task<bool> EvalArrowAsync(
        SchemaIndex index, TenantContext tenant, EntityRef obj, Arrow arrow,
        SubjectRef subject, RequestContext context, EvalContext ctx,
        List<ExplainNode>? explain, CancellationToken ct)
    {
        var edges = await _relations.GetByObjectAsync(tenant, obj, arrow.Relation, ct);
        foreach (var edge in edges)
        {
            if (!await ConditionSatisfiedAsync(index, tenant, obj, edge, context, ctx, ct))
                continue;

            // The subject of a structural-reference tuple is the related entity.
            var related = new EntityRef(edge.Subject.Type, edge.Subject.Id);

            bool hit;
            if (index.TryPermission(related.Type, arrow.Permission, out _))
                hit = await CheckPermissionAsync(index, tenant, related, arrow.Permission, subject, context, ctx, explain, ct);
            else
                hit = await ResolveRelationAsync(index, tenant, related, arrow.Permission, subject, context, ctx, ct);

            if (hit) return true;
        }
        return false;
    }

    /// <summary>
    /// Branch-level condition gate (a <see cref="Conditioned"/> node). Evaluated with
    /// empty parameters against request context + the object's synced attributes.
    /// Latches <see cref="EvalContext.ConditionTouched"/>.
    /// </summary>
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
}
```

> Note: the `Union` branch above intentionally evaluates `Right` even when explaining, so the explain tree records both branches; the early `return true` only fires when `explain is null`. The same pattern applies to `Intersect`/`Exclude` (short-circuit only when not explaining), so the explain trace is always the full subtree while non-explain checks still short-circuit.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter AlgebraTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.Expr.cs tests/Custodex.Core.Tests/Evaluation/AlgebraTests.cs
git commit -m "feat: implement full permission algebra in engine-driven check"
```

---

### Task 6: The quarantine structural gate (worked example 12.5, end to end)

**Files:**
- Test: `tests/Custodex.Core.Tests/Evaluation/QuarantineGateTests.cs`

**Interfaces:**
- Consumes: `EngineDrivenAuthorizer`, the full algebra (Task 5). No new production code — this task pins the discriminating worked example that proves the pointwise model on intersection + exclusion + arrow + wildcard together.

> This is the case from spec §12.5. `base_access` is concretely modelled here as a single relation `can_access` (the spec leaves it abstract). The gate:
> ```
> enclosure.is_quarantine = is_quarantine
> animal.access =
>     ( can_access - enclosure->is_quarantine )
>   + ( enclosure->is_quarantine & vets_or_nurses & trained )
> ```
> with `vets_or_nurses = vet_member + vet_nurse_member` and `trained = trained_member`, each backed by a relation that resolves through group membership. Wildcard `enclosure:Q1#is_quarantine@user:*` marks Q1 as quarantine (universal set).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/Evaluation/QuarantineGateTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class QuarantineGateTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("enclosure", t => t
            .Relation("is_quarantine", s => s.Wildcard("user"))
            .Permission("is_quarantine", p => p.Relation("is_quarantine")))
        .Type("animal", t => t
            .Relation("can_access", s => s.User().SubjectSet("group", "member"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Relation("vet_member", s => s.SubjectSet("group", "member"))
            .Relation("vet_nurse_member", s => s.SubjectSet("group", "member"))
            .Relation("trained_member", s => s.SubjectSet("group", "member"))
            .Permission("access", p => p
                .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("enclosure", "is_quarantine")))
                .Union(b => b
                    .Arrow("enclosure", "is_quarantine")
                    .Intersect(x => x.Relation("vet_member").Union(y => y.Relation("vet_nurse_member")))
                    .Intersect(x => x.Relation("trained_member")))))
        .Build();

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static CheckRequest Access(string animal, string user) => new(
        T, new EntityRef("animal", animal), "access", new SubjectRef("user", user),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()));

    // Common membership wiring: dr-smith is a vet and trained; jones is a vet but untrained.
    private static RelationTuple[] Members(string animal) =>
    [
        Tuple("animal", animal, "vet_member", new SubjectRef("group", "vets", "member")),
        Tuple("animal", animal, "trained_member", new SubjectRef("group", "trained", "member")),
        Tuple("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        Tuple("group", "vets", "member", new SubjectRef("user", "jones")),
        Tuple("group", "trained", "member", new SubjectRef("user", "dr-smith")),
        Tuple("animal", animal, "can_access", new SubjectRef("user", "dr-smith")),
        Tuple("animal", animal, "can_access", new SubjectRef("user", "jones")),
    ];

    [Fact]
    public async Task Trained_vet_inside_quarantine_is_allowed()
    {
        var tuples = new List<RelationTuple>(Members("EL-001"))
        {
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        var auth = await NewAsync(Build(), tuples.ToArray());
        (await auth.CheckAsync(Access("EL-001", "dr-smith"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Untrained_vet_inside_quarantine_is_denied()
    {
        var tuples = new List<RelationTuple>(Members("EL-001"))
        {
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        var auth = await NewAsync(Build(), tuples.ToArray());
        // jones has can_access, but base is revoked inside quarantine and jones is untrained.
        (await auth.CheckAsync(Access("EL-001", "jones"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Outside_quarantine_base_access_passes_through()
    {
        // EL-002 is in a normal enclosure (no is_quarantine wildcard tuple).
        var tuples = new List<RelationTuple>(Members("EL-002"))
        {
            Tuple("animal", "EL-002", "enclosure", new SubjectRef("enclosure", "KH1")),
        };
        var auth = await NewAsync(Build(), tuples.ToArray());
        (await auth.CheckAsync(Access("EL-002", "jones"))).Allowed.ShouldBeTrue();   // base access intact
        (await auth.CheckAsync(Access("EL-002", "dr-smith"))).Allowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify (expect PASS if Task 5 is correct)**

Run: `dotnet test tests/Custodex.Core.Tests --filter QuarantineGateTests`
Expected: PASS (3 tests). If any fails, the algebra in Task 5 is wrong for nested intersection/exclusion-through-arrow — fix Task 5, not the test. This is the discriminating case.

- [ ] **Step 3: Commit**

```bash
git add tests/Custodex.Core.Tests/Evaluation/QuarantineGateTests.cs
git commit -m "test: pin quarantine structural-gate worked example (12.5)"
```

---

### Task 7: Per-request memoization, Explain tree, diagnostics span + histogram

**Files:**
- Modify: `src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs`
- Test: `tests/Custodex.Core.Tests/Evaluation/CheckObservabilityTests.cs`

**Interfaces:**
- Produces: `CheckAsync` wrapped in a `CustodexDiagnostics.ActivitySource` span recording the decision, and recording `CustodexDiagnostics.CheckDuration`; `CheckResult.Explain` populated when `CheckRequest.Explain`. The internal `ConditionTouched` flag is exposed to `m0/08` via an internal richer-result path (below).
- Consumes: `CustodexDiagnostics` (from `m0/01`); `EvalContext.ConditionTouched`.

> **Memo vs Explain.** The per-request memo caches `(object,permission,subject)→bool`. When `Explain` is requested we **bypass the memo** so the trace is the full tree rather than a leaf "(memoized)" stub — `CheckPermissionAsync` already only reads/writes the memo when `explain is null` (Task 4). Non-explain checks keep memoization and so dedupe repeated sub-checks within one request.
>
> **Conditioned flag for the cache (m0/08).** The public `CheckResult` cannot express "this result depended on a condition," yet `m0/08` may cache only unconditioned results. `EngineDrivenAuthorizer` exposes an **internal** entry point returning both the decision and the `ConditionTouched` flag so the caching decorator can decide cacheability without a public-contract change. See Contract gaps.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/Evaluation/CheckObservabilityTests.cs
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class CheckObservabilityTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<EngineDrivenAuthorizer> NewAsync()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("editor", s => s.User())
                .Permission("access", p => p.Relation("viewer").Union(x => x.Relation("editor"))))
            .Build();
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await relations.WriteAsync(T,
            new[] { new RelationTuple(new EntityRef("doc", "D1"), "editor", new SubjectRef("user", "alice")) },
            Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest Req(bool explain) => new(
        T, new EntityRef("doc", "D1"), "access", new SubjectRef("user", "alice"),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"),
            new Dictionary<string, object?>()), Explain: explain);

    [Fact]
    public async Task Explain_tree_is_populated_when_requested_and_null_otherwise()
    {
        var auth = await NewAsync();
        var plain = await auth.CheckAsync(Req(explain: false));
        plain.Allowed.ShouldBeTrue();
        plain.Explain.ShouldBeNull();

        var explained = await auth.CheckAsync(Req(explain: true));
        explained.Allowed.ShouldBeTrue();
        explained.Explain.ShouldNotBeNull();
        explained.Explain!.Description.ShouldContain("access");
        // The union branch and its two relation children are present.
        explained.Explain.Children.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Check_emits_an_activity_span()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Custodex",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);

        var auth = await NewAsync();
        await auth.CheckAsync(Req(explain: false));

        captured.ShouldContain(a => a.OperationName == "Custodex.check");
    }

    [Fact]
    public async Task Check_records_duration_histogram()
    {
        var measured = false;
        using var mlistener = new MeterListener();
        mlistener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Custodex" && inst.Name == "Custodex.check.duration")
                l.EnableMeasurementEvents(inst);
        };
        mlistener.SetMeasurementEventCallback<double>((_, _, _, _) => measured = true);
        mlistener.Start();

        var auth = await NewAsync();
        await auth.CheckAsync(Req(explain: false));

        measured.ShouldBeTrue();
    }

    [Fact]
    public async Task Internal_check_reports_condition_touched_flag()
    {
        var auth = await NewAsync();
        var (allowed, conditionTouched) = await auth.CheckInternalAsync(Req(explain: false));
        allowed.ShouldBeTrue();
        conditionTouched.ShouldBeFalse();   // no conditions on this schema
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter CheckObservabilityTests`
Expected: FAIL — no span/histogram emitted; `CheckInternalAsync` not defined; explain root may be missing.

- [ ] **Step 3: Expose internals to the test project**

`CheckInternalAsync` is `internal`, and `CheckObservabilityTests` (in `Custodex.Core.Tests`) calls it. Make `Custodex.Core` internals visible to the test assembly. Add to `src/Custodex.Core/Custodex.Core.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Custodex.Core.Tests" />
</ItemGroup>
```

> `m0/07` Task 1 references this same `InternalsVisibleTo` ("if not already present"); it is introduced here in `m0/05` because this is where the first `internal` member consumed by a test appears.

- [ ] **Step 4: Replace `CheckAsync` with the instrumented + internal-result form**

```csharp
// In src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs — replace the CheckAsync from Task 4.
    public async Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        var (allowed, _, explain) = await RunCheckAsync(request, ct);
        return new CheckResult(allowed, explain);
    }

    /// <summary>
    /// Internal entry point used by the m0/08 caching decorator: returns the decision
    /// together with whether any condition was reached, so the cache can avoid storing
    /// condition-dependent results. Never emits an Explain tree (caching path).
    /// </summary>
    internal async Task<(bool Allowed, bool ConditionTouched)> CheckInternalAsync(
        CheckRequest request, CancellationToken ct = default)
    {
        var (allowed, conditionTouched, _) = await RunCheckAsync(
            request with { Explain = false }, ct);
        return (allowed, conditionTouched);
    }

    private async Task<(bool Allowed, bool ConditionTouched, ExplainNode? Explain)> RunCheckAsync(
        CheckRequest request, CancellationToken ct)
    {
        using var activity = CustodexDiagnostics.ActivitySource.StartActivity("Custodex.check");
        activity?.SetTag("Custodex.object", request.Object.ToString());
        activity?.SetTag("Custodex.permission", request.Permission);
        activity?.SetTag("Custodex.subject", request.Subject.ToString());

        var start = Stopwatch.GetTimestamp();
        try
        {
            var index = await LoadSchemaAsync(request.Tenant.Store, ct);
            var ctx = new EvalContext(_options);
            var roots = request.Explain ? new List<ExplainNode>() : null;
            var allowed = await CheckPermissionAsync(
                index, request.Tenant, request.Object, request.Permission, request.Subject,
                request.Context, ctx, roots, ct);

            activity?.SetTag("Custodex.allowed", allowed);
            activity?.SetTag("Custodex.condition_touched", ctx.ConditionTouched);
            return (allowed, ctx.ConditionTouched, roots is { Count: > 0 } ? roots[0] : null);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            CustodexDiagnostics.CheckDuration.Record(elapsedMs);
        }
    }
```

> Remove the original `CheckAsync` body from Task 4 (the one that built its own `EvalContext` inline); `RunCheckAsync` is now the single evaluation entry. `CheckPermissionAsync`, `EvalExprAsync`, and the membership helpers are unchanged.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter CheckObservabilityTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Run the whole Check suite**

Run: `dotnet test tests/Custodex.Core.Tests --filter Evaluation`
Expected: PASS (all Evaluation tests: EvalContext, SubjectMembership, Algebra, QuarantineGate, CheckObservability).

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Core/Evaluation/EngineDrivenAuthorizer.cs tests/Custodex.Core.Tests/Evaluation/CheckObservabilityTests.cs
git commit -m "feat: add explain tree, diagnostics, and internal condition-touched result to check"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Check is **pointwise** (membership of the query subject), not set materialization — verified by the quarantine gate (Task 6).
- [ ] Cycle on the DFS path prunes to deny; depth bound throws `EvaluationLimitException` (Task 1).
- [ ] Arrow recurses into the related object's full permission expression, so inner exclusions are honoured (Task 5, `Arrow_sees_inner_exclusion_on_the_related_object`).
- [ ] Wildcard `type:*` grants every subject of that type (Task 4).
- [ ] Per-request memo dedupes non-explain sub-checks; explain bypasses the memo for a full tree (Task 7).
- [ ] One `Custodex.check` Activity per check; `Custodex.check.duration` recorded every check (Task 7).
- [ ] `ListObjects`/`ListSubjects`/`BatchCheck` remain `NotImplementedException` for `m0/07` to fill.

## Contract gaps (reported, not changed)

- **`CheckResult` cannot signal condition-dependence.** The public `CheckResult(bool Allowed, ExplainNode? Explain)` has no field indicating whether the decision touched a condition, but `m0/08` must cache only unconditioned results. Resolved **without** editing `README.md` by exposing an internal `EngineDrivenAuthorizer.CheckInternalAsync` returning `(bool Allowed, bool ConditionTouched)`. If a future revision wants this on the public surface, add a flag to `CheckResult` in the contract first.
- **`IConditionEvaluator` seam vs `m0/06`'s `ConditionEvaluator` (shape mismatch — reconcile during execution).** This plan introduces `Custodex.Core.Conditions.IConditionEvaluator` (`bool Evaluate(ConditionDef, ConditionRef, IReadOnlyDictionary<string,object?>, RequestContext)`) and `NullConditionEvaluator` as the injectable seam the authorizer consumes. The already-written `m0/06` ships `ConditionEvaluator` as a **static class** returning `ConditionResult` with parameter order `(ConditionDef, attributes, RequestContext, parameters)` — it does **not** implement `IConditionEvaluator`. These are reconciled by a thin adapter `CelConditionEvaluator : IConditionEvaluator` that `m0/06` should add (forwarding to the static method, mapping `ConditionRef.Parameters → parameters` and `ConditionResult.Allow → true`, Deny/Error → false). Neither type is in `README.md`'s public contract (both are `Custodex.Core` internals), so no contract edit is required; this is a cross-plan integration note for the orchestrator — `m0/06` owns adding the adapter, this plan owns the seam. No `README.md` change made, and `m0/06` was not edited.
