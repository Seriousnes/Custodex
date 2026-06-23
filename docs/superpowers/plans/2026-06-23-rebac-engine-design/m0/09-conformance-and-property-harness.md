# M0/09 — Conformance & Property Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A declarative conformance case format (`schema + tuples + attributes + (subject,object,permission,context) → expected`), an xUnit runner in a new `Relkit.Conformance` project encoding the six worked examples from spec §12 as named cases, and CsCheck property tests asserting algebra invariants against the engine-driven authorizer over the in-memory store.

**Architecture:** A `ConformanceCase` record carries everything needed to run one decision deterministically: a `Schema`, the tuples and attributes to load, and one `(subject, object, permission, context)` query with its expected `Allowed`. A `ConformanceRunner` materializes the case against `Relkit.Storage.InMemory` and the `EngineDrivenAuthorizer`, runs `CheckAsync`, and asserts. The six worked examples are authored as named cases (their schemas are concrete and must pass `m0/03` validation). CsCheck generators build random unconditioned schemas + tuple sets and assert algebra laws (`a - a = deny`, wildcard grants everyone, union monotonicity over exclusion-free expressions, nesting reachability) — using the engine-driven authorizer as the executable specification.

**Tech Stack:** .NET 10, C# 14, xUnit, Shouldly, **CsCheck** (MIT). Uses `Relkit.Storage.InMemory` (from `m0/04`) and `EngineDrivenAuthorizer` + `NullConditionEvaluator` (from `m0/05`–`m0/07`).

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. **CsCheck is the property-test library** (MIT, permissive). Depends on `m0/02`–`m0/07`.

> **Calibration (the rigor lives here).** The engine code in `m0/05`–`m0/07` is a candidate to be proven. *These* cases and properties are the durable specification: the six worked examples pin real, checkable behaviour, and the algebra laws pin the operators. Write them to be correct now; if the engine disagrees with a case, the engine is wrong (unless the case itself is shown to misread the spec).

---

### Task 1: Create `Relkit.Conformance` and the case format

**Files:**
- Create: `tests/Relkit.Conformance/Relkit.Conformance.csproj`
- Create: `tests/Relkit.Conformance/ConformanceCase.cs`
- Test: `tests/Relkit.Conformance/CaseFormatTests.cs`

**Interfaces:**
- Produces: the `Relkit.Conformance` project (referencing `Relkit.Core`, `Relkit.Storage.InMemory`, `Relkit.Abstractions`); the `ConformanceCase` and `AttributeSeed` records.
- Consumes: `Schema`, `RelationTuple`, `EntityRef`, `SubjectRef`, `RequestContext` from `Relkit.Abstractions`.

- [ ] **Step 1: Create the project and references**

Run:
```bash
dotnet new xunit -n Relkit.Conformance -o tests/Relkit.Conformance -f net10.0
rm tests/Relkit.Conformance/UnitTest1.cs
dotnet sln add tests/Relkit.Conformance
dotnet add tests/Relkit.Conformance reference src/Relkit.Abstractions
dotnet add tests/Relkit.Conformance reference src/Relkit.Core
dotnet add tests/Relkit.Conformance reference src/Relkit.Storage.InMemory
dotnet add tests/Relkit.Conformance package Shouldly
dotnet add tests/Relkit.Conformance package CsCheck
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Relkit.Conformance/CaseFormatTests.cs
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Conformance;

public class CaseFormatTests
{
    [Fact]
    public void Case_carries_schema_tuples_query_and_expectation()
    {
        var schema = new Relkit.Core.SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();
        var c = new ConformanceCase(
            Name: "direct grant",
            Schema: schema,
            Tuples: new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")) },
            Attributes: Array.Empty<AttributeSeed>(),
            Object: new EntityRef("doc", "D1"),
            Permission: "view",
            Subject: new SubjectRef("user", "alice"),
            Now: DateTimeOffset.UnixEpoch,
            Context: new Dictionary<string, object?>(),
            Expected: true);

        c.Name.ShouldBe("direct grant");
        c.Expected.ShouldBeTrue();
        c.Tuples.Count.ShouldBe(1);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Relkit.Conformance --filter CaseFormatTests`
Expected: FAIL — `ConformanceCase` not defined.

- [ ] **Step 4: Define the case records**

```csharp
// tests/Relkit.Conformance/ConformanceCase.cs
using Relkit.Abstractions;

namespace Relkit.Conformance;

/// <summary>Synced resource attributes to seed for one object before the query runs.</summary>
public sealed record AttributeSeed(EntityRef Object, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// One declarative authorization decision: load <see cref="Schema"/>, <see cref="Tuples"/>
/// and <see cref="Attributes"/>, then Check (<see cref="Subject"/>, <see cref="Object"/>,
/// <see cref="Permission"/>) under <see cref="Now"/> + <see cref="Context"/>, expecting
/// <see cref="Expected"/>.
/// </summary>
public sealed record ConformanceCase(
    string Name,
    Schema Schema,
    IReadOnlyList<RelationTuple> Tuples,
    IReadOnlyList<AttributeSeed> Attributes,
    EntityRef Object,
    string Permission,
    SubjectRef Subject,
    DateTimeOffset Now,
    IReadOnlyDictionary<string, object?> Context,
    bool Expected);
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Conformance --filter CaseFormatTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/Relkit.Conformance
git commit -m "feat: add conformance project and declarative case format"
```

---

### Task 2: The conformance runner

**Files:**
- Create: `tests/Relkit.Conformance/ConformanceRunner.cs`
- Test: `tests/Relkit.Conformance/RunnerTests.cs`

**Interfaces:**
- Produces: `static Task<CheckResult> ConformanceRunner.RunAsync(ConformanceCase c, CancellationToken ct = default)` — materializes the case against `Relkit.Storage.InMemory`, validates the schema (so a malformed worked example fails loudly), seeds tuples + attributes, builds the `EngineDrivenAuthorizer`, and runs `CheckAsync`. Also `static Task AssertAsync(ConformanceCase c)` asserting `Allowed == Expected` with the case name in the message.
- Consumes: `EngineDrivenAuthorizer`, `NullConditionEvaluator`, the in-memory stores, and the `m0/03` schema validator.

> The runner uses the real validation path (`SchemaValidator` from `m0/03`) so that an incorrectly authored worked-example schema (dangling relation, non-terminating recursion) fails as a `SchemaValidationException` rather than silently producing a wrong decision. The fixed tenant is `("conformance","t")`. The `NullConditionEvaluator` is used for cases without conditions; cases that use conditions supply a real evaluator via an overload once `m0/06` lands (noted in the task).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Conformance/RunnerTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Shouldly;
using Xunit;

namespace Relkit.Conformance;

public class RunnerTests
{
    [Fact]
    public async Task Runs_a_simple_allow_case()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();
        var c = new ConformanceCase("allow", schema,
            new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")) },
            Array.Empty<AttributeSeed>(),
            new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);

        var result = await ConformanceRunner.RunAsync(c);
        result.Allowed.ShouldBeTrue();
        await ConformanceRunner.AssertAsync(c);   // should not throw
    }

    [Fact]
    public async Task Runs_a_simple_deny_case()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();
        var c = new ConformanceCase("deny", schema,
            Array.Empty<RelationTuple>(), Array.Empty<AttributeSeed>(),
            new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);

        await ConformanceRunner.AssertAsync(c);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Conformance --filter RunnerTests`
Expected: FAIL — `ConformanceRunner` not defined.

- [ ] **Step 3: Implement the runner**

```csharp
// tests/Relkit.Conformance/ConformanceRunner.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;

namespace Relkit.Conformance;

public static class ConformanceRunner
{
    private static readonly TenantContext Tenant = new("conformance", "t");

    public static async Task<CheckResult> RunAsync(ConformanceCase c, CancellationToken ct = default)
    {
        // Validate the schema through the real m0/03 validator so malformed cases fail loudly.
        // SchemaValidator is a static class (m0/03); EmptyConditionBody passes validation there
        // (body type-checking is deferred to m0/06), so the 12.6 within_hours case validates.
        var validation = SchemaValidator.Validate(c.Schema);
        if (!validation.IsValid)
            throw new SchemaValidationException(validation.Errors);

        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();

        await schemaStore.SetActiveAsync(Tenant.Store, c.Schema, uow, ct);
        if (c.Tuples.Count > 0)
            await relations.WriteAsync(Tenant, c.Tuples, Array.Empty<RelationTuple>(), uow, ct);
        foreach (var seed in c.Attributes)
            await attributes.SetAsync(Tenant, seed.Object, seed.Attributes, uow, ct);
        await uow.CommitAsync(ct);

        var authorizer = new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());

        var request = new CheckRequest(
            Tenant, c.Object, c.Permission, c.Subject,
            new RequestContext(c.Now, c.Subject, c.Context));
        return await authorizer.CheckAsync(request, ct);
    }

    public static async Task AssertAsync(ConformanceCase c)
    {
        var result = await RunAsync(c);
        result.Allowed.ShouldBe(c.Expected,
            $"Conformance case '{c.Name}': expected Allowed={c.Expected} for " +
            $"{c.Subject} on {c.Object}#{c.Permission}, got {result.Allowed}.");
    }
}
```

> `SchemaValidator` and its `Validate(Schema) -> SchemaValidationResult` come from `m0/03`. If `m0/03` named the entry point differently, adjust this single call. The runner deliberately routes through validation so the conformance suite doubles as a validation acceptance test for the worked-example schemas.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Conformance --filter RunnerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Conformance/ConformanceRunner.cs tests/Relkit.Conformance/RunnerTests.cs
git commit -m "feat: add conformance runner over in-memory provider"
```

---

### Task 3: Worked examples 12.1–12.4 as named cases

**Files:**
- Create: `tests/Relkit.Conformance/WorkedExamples.cs`
- Create: `tests/Relkit.Conformance/WorkedExamplesTests.cs`

**Interfaces:**
- Produces: `WorkedExamples.RoleGrantOverCategory()`, `.TeamGrantOverCuratedSet()`, `.SiteScopedAccess()`, `.DirectGrantOnInstance()` — each returns one or more `ConformanceCase` covering the spec §12 example, with a complete, validation-passing schema. A `[Theory]` runs each.
- Consumes: `ConformanceCase`, `SchemaBuilder`, `ConformanceRunner`.

> Each worked-example schema is made concrete and complete (every arrow target exists as a permission on the target type; no dangling relations) so it passes `m0/03` validation. Where the spec snippet is abstract (e.g. relation lists), this task fills in the minimal concrete schema that realizes the example.

- [ ] **Step 1: Write the cases and the theory test (failing until the cases compile and run)**

```csharp
// tests/Relkit.Conformance/WorkedExamples.cs
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Conformance;

public static partial class WorkedExamples
{
    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    // 12.1 — "vets may record drug dispensing" via a resource category.
    // inventory_item.record_dispense = dispenser + category->record_dispense - blocked
    public static ConformanceCase RoleGrantOverCategory()
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
                .Permission("record_dispense", p => p
                    .Relation("dispenser")
                    .Arrow("category", "record_dispense")
                    .Exclude(x => x.Relation("blocked"))))
            .Build();

        var tuples = new[]
        {
            T("category", "drugs", "dispenser", new SubjectRef("group", "vets", "member")),
            T("inventory_item", "vaccine-X", "category", new SubjectRef("category", "drugs")),
            T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        };

        return new ConformanceCase("12.1 role grant over category", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("inventory_item", "vaccine-X"), "record_dispense", new SubjectRef("user", "dr-smith"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.2 — "the macropods round may edit a hand-picked species list".
    public static ConformanceCase TeamGrantOverCuratedSet()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("species", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")))
            .Build();

        var tuples = new[]
        {
            T("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            T("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            T("group", "macropods", "member", new SubjectRef("user", "alice")),
        };

        return new ConformanceCase("12.2 team grant over curated set", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("species", "kangaroo"), "edit", new SubjectRef("user", "alice"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.3 — "keepers edit Sydney enclosures, not other sites".
    // enclosure.edit = can_edit + site->edit - blocked
    public static ConformanceCase SiteScopedAccess()
    {
        var schema = SiteScopedSchema();
        var tuples = new[]
        {
            T("site", "sydney", "can_edit", new SubjectRef("group", "keepers", "member")),
            T("enclosure", "KH1", "site", new SubjectRef("site", "sydney")),
            T("group", "keepers", "member", new SubjectRef("user", "kim")),
        };
        return new ConformanceCase("12.3 site-scoped access (sydney allowed)", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("enclosure", "KH1"), "edit", new SubjectRef("user", "kim"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.3 negative — Melbourne enclosure is untouched by the Sydney grant.
    public static ConformanceCase SiteScopedAccessOtherSiteDenied()
    {
        var schema = SiteScopedSchema();
        var tuples = new[]
        {
            T("site", "sydney", "can_edit", new SubjectRef("group", "keepers", "member")),
            T("enclosure", "MEL1", "site", new SubjectRef("site", "melbourne")),
            T("group", "keepers", "member", new SubjectRef("user", "kim")),
        };
        return new ConformanceCase("12.3 site-scoped access (melbourne denied)", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("enclosure", "MEL1"), "edit", new SubjectRef("user", "kim"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);
    }

    private static Schema SiteScopedSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("site", t => t
            .Relation("can_edit", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("can_edit")))
        .Type("enclosure", t => t
            .Relation("can_edit", s => s.User().SubjectSet("group", "member"))
            .Relation("site", s => s.Type("site"))
            .Relation("blocked", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p
                .Relation("can_edit")
                .Arrow("site", "edit")
                .Exclude(x => x.Relation("blocked"))))
        .Build();

    // 12.4 — direct grant on one instance.
    public static ConformanceCase DirectGrantOnInstance()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("can_manage", s => s.User())
                .Permission("manage", p => p.Relation("can_manage")))
            .Build();
        var tuples = new[] { T("animal", "EL-001", "can_manage", new SubjectRef("user", "carol")) };
        return new ConformanceCase("12.4 direct grant on instance", schema, tuples,
            Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-001"), "manage", new SubjectRef("user", "carol"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }
}
```

```csharp
// tests/Relkit.Conformance/WorkedExamplesTests.cs
using Xunit;

namespace Relkit.Conformance;

public class WorkedExamplesTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        [WorkedExamples.RoleGrantOverCategory()],
        [WorkedExamples.TeamGrantOverCuratedSet()],
        [WorkedExamples.SiteScopedAccess()],
        [WorkedExamples.SiteScopedAccessOtherSiteDenied()],
        [WorkedExamples.DirectGrantOnInstance()],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Worked_example_holds(ConformanceCase c) => await ConformanceRunner.AssertAsync(c);
}
```

- [ ] **Step 2: Run to verify (expect PASS if engine + validator are correct)**

Run: `dotnet test tests/Relkit.Conformance --filter WorkedExamplesTests`
Expected: PASS (5 cases). A failure here means either the engine (`m0/05`) is wrong for this shape, or the schema/tuples misread the spec — diagnose before changing anything.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Conformance/WorkedExamples.cs tests/Relkit.Conformance/WorkedExamplesTests.cs
git commit -m "test: encode worked examples 12.1-12.4 as conformance cases"
```

---

### Task 4: Worked example 12.5 (structural gate) and 12.6 (condition) as cases

**Files:**
- Create: `tests/Relkit.Conformance/WorkedExamples.Gate.cs`
- Create: `tests/Relkit.Conformance/WorkedExamplesGateTests.cs`

**Interfaces:**
- Produces: `WorkedExamples.QuarantineTrainedVetAllowed()`, `.QuarantineUntrainedVetDenied()`, `.OutsideQuarantineBaseAccess()` (12.5), and `WorkedExamples.TimeBoundedDispenseWithinHours()` / `.TimeBoundedDispenseOutsideHours()` (12.6) as `ConformanceCase`s.
- Consumes: the `m0/05` quarantine schema shape; for 12.6, the condition path — which uses `NullConditionEvaluator` in M0 (so the time gate is asserted via the model shape; the real time check is exercised in `m0/06`).

> **12.5** reuses the pointwise structural gate proved in `m0/05` Task 6, expressed declaratively here so it lives in the portable conformance suite. **12.6** is a time-bounded dispense: in M0 the `NullConditionEvaluator` treats the condition as satisfied, so the case asserts the *structural* grant resolves (the conditioned branch is reached). A note marks that `m0/06` re-runs 12.6 with the real `within_hours` evaluator and a second case for the out-of-hours deny; here the in-hours case asserts `true` under the null evaluator and the out-of-hours case is authored but skipped until `m0/06` provides the real evaluator.

- [ ] **Step 1: Write the gate + condition cases**

```csharp
// tests/Relkit.Conformance/WorkedExamples.Gate.cs
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Conformance;

public static partial class WorkedExamples
{
    // 12.5 — "only quarantine-trained vets may access animals in a quarantine enclosure".
    // animal.access = (can_access - enclosure->is_quarantine)
    //               + (enclosure->is_quarantine & (vet_member + vet_nurse_member) & trained_member)
    private static Schema QuarantineSchema() => new SchemaBuilder("v1")
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

    private static RelationTuple[] QuarantineMembership(string animal) =>
    [
        T("animal", animal, "vet_member", new SubjectRef("group", "vets", "member")),
        T("animal", animal, "trained_member", new SubjectRef("group", "trained", "member")),
        T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        T("group", "vets", "member", new SubjectRef("user", "jones")),
        T("group", "trained", "member", new SubjectRef("user", "dr-smith")),
        T("animal", animal, "can_access", new SubjectRef("user", "dr-smith")),
        T("animal", animal, "can_access", new SubjectRef("user", "jones")),
    ];

    public static ConformanceCase QuarantineTrainedVetAllowed()
    {
        var tuples = new List<RelationTuple>(QuarantineMembership("EL-001"))
        {
            T("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            T("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        return new ConformanceCase("12.5 trained vet inside quarantine allowed", QuarantineSchema(),
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-001"), "access", new SubjectRef("user", "dr-smith"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    public static ConformanceCase QuarantineUntrainedVetDenied()
    {
        var tuples = new List<RelationTuple>(QuarantineMembership("EL-001"))
        {
            T("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            T("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        return new ConformanceCase("12.5 untrained vet inside quarantine denied", QuarantineSchema(),
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-001"), "access", new SubjectRef("user", "jones"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: false);
    }

    public static ConformanceCase OutsideQuarantineBaseAccess()
    {
        var tuples = new List<RelationTuple>(QuarantineMembership("EL-002"))
        {
            T("animal", "EL-002", "enclosure", new SubjectRef("enclosure", "KH1")),   // not quarantine
        };
        return new ConformanceCase("12.5 outside quarantine base access passes", QuarantineSchema(),
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("animal", "EL-002"), "access", new SubjectRef("user", "jones"),
            DateTimeOffset.UnixEpoch, new Dictionary<string, object?>(), Expected: true);
    }

    // 12.6 — time-bounded dispensing. The condition rides on the dispenser tuple.
    // In M0 the NullConditionEvaluator treats within_hours as satisfied, so this asserts
    // the structural grant resolves and the conditioned branch is reached.
    public static ConformanceCase TimeBoundedDispenseWithinHours()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("category", t => t
                .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
                .Permission("record_dispense", p => p.Relation("dispenser")))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();

        var tuples = new[]
        {
            new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
                new SubjectRef("group", "vets", "member"),
                new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 })),
            T("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        };

        var context = new Dictionary<string, object?>();
        return new ConformanceCase("12.6 time-bounded dispense (in hours, null-eval)", schema,
            tuples, Array.Empty<AttributeSeed>(),
            new EntityRef("category", "drugs"), "record_dispense", new SubjectRef("user", "dr-smith"),
            new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), context, Expected: true);
    }
}
```

```csharp
// tests/Relkit.Conformance/WorkedExamplesGateTests.cs
using Xunit;

namespace Relkit.Conformance;

public class WorkedExamplesGateTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        [WorkedExamples.QuarantineTrainedVetAllowed()],
        [WorkedExamples.QuarantineUntrainedVetDenied()],
        [WorkedExamples.OutsideQuarantineBaseAccess()],
        [WorkedExamples.TimeBoundedDispenseWithinHours()],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Gate_and_condition_examples_hold(ConformanceCase c) => await ConformanceRunner.AssertAsync(c);
}
```

> **Note for `m0/06`:** add an out-of-hours twin (`TimeBoundedDispenseOutsideHours()` with `Now` at hour 22, `Expected: false`) and re-run 12.6 through a runner overload that injects the real `ConditionEvaluator`. The `within_hours` body and parameters are already on the schema here; only the evaluator changes.

- [ ] **Step 2: Run to verify (expect PASS)**

Run: `dotnet test tests/Relkit.Conformance --filter WorkedExamplesGateTests`
Expected: PASS (4 cases). The three 12.5 cases are the discriminating structural-gate proof; if any fails, the algebra in `m0/05` is wrong — fix the engine, not the case.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Conformance/WorkedExamples.Gate.cs tests/Relkit.Conformance/WorkedExamplesGateTests.cs
git commit -m "test: encode quarantine gate (12.5) and time-bounded dispense (12.6) as cases"
```

---

### Task 5: CsCheck property tests — algebra invariants

**Files:**
- Create: `tests/Relkit.Conformance/Properties/AlgebraGenerators.cs`
- Create: `tests/Relkit.Conformance/Properties/AlgebraInvariantTests.cs`

**Interfaces:**
- Produces: CsCheck generators building random tuple sets over a small fixed schema, and property tests asserting: `a - a = deny`, wildcard grants everyone, union monotonicity (over exclusion/condition-free expressions), and nesting reachability.
- Consumes: `EngineDrivenAuthorizer`, `NullConditionEvaluator`, the in-memory stores, CsCheck `Gen`.

> **Property scoping.** `Exclude` is non-monotone, so a blanket "adding tuples only adds allows" is false. The monotonicity property is therefore scoped to an **exclusion- and condition-free** permission (`access = viewer`): there, granting `viewer` to a subject can only flip a deny to an allow, never the reverse. The other laws hold generally:
> - **`a - a = deny`**: for any subject and any tuple set, `access = viewer - viewer` denies (proved structurally; the engine must agree for arbitrary data).
> - **Wildcard grants everyone**: with `viewer@user:*`, every randomly chosen user is allowed on `view`.
> - **Nesting reachability**: a user reachable through a random chain of nested `group#member` edges holds a permission granted to the top group.

- [ ] **Step 1: Write the generators**

```csharp
// tests/Relkit.Conformance/Properties/AlgebraGenerators.cs
using CsCheck;
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Conformance.Properties;

public static class AlgebraGenerators
{
    public static readonly TenantContext Tenant = new("prop", "t");

    // A small pool of user ids the generators draw from.
    public static readonly Gen<string> UserId = Gen.OneOf(
        Gen.Const("u1"), Gen.Const("u2"), Gen.Const("u3"), Gen.Const("u4"), Gen.Const("u5"));

    // A schema with an exclusion-free, condition-free permission for the monotonicity law.
    public static Schema MonotoneSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Permission("view", p => p.Relation("viewer")))
        .Build();

    // A schema whose permission is a - a (self-exclusion).
    public static Schema SelfExcludeSchema() => new SchemaBuilder("v1")
        .Type("doc", t => t
            .Relation("viewer", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("viewer"))))
        .Build();

    // A chain of nested groups g0 < g1 < ... < gN, with a user at the bottom and a grant at the top.
    public static readonly Gen<int> ChainLength = Gen.Int[1, 6];

    public static IReadOnlyList<RelationTuple> NestedChainTuples(int length, string user)
    {
        var tuples = new List<RelationTuple>
        {
            // doc:D1#viewer@group:g0#member
            new(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "g0", "member")),
        };
        for (var i = 0; i < length - 1; i++)
            tuples.Add(new RelationTuple(new EntityRef("group", $"g{i}"), "member",
                new SubjectRef("group", $"g{i + 1}", "member")));
        // bottom group gets the user
        tuples.Add(new RelationTuple(new EntityRef("group", $"g{length - 1}"), "member",
            new SubjectRef("user", user)));
        return tuples;
    }
}
```

```csharp
// tests/Relkit.Conformance/Properties/AlgebraInvariantTests.cs
using CsCheck;
using Relkit.Abstractions;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Conformance.Properties;

public class AlgebraInvariantTests
{
    private static readonly TenantContext T = AlgebraGenerators.Tenant;

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, IReadOnlyList<RelationTuple> tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new InMemoryUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Count > 0) await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest View(string user) => new(
        T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", user),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()));

    [Fact]
    public void SelfExclusion_always_denies()
    {
        AlgebraGenerators.UserId.SampleAsync(async user =>
        {
            var auth = await NewAsync(AlgebraGenerators.SelfExcludeSchema(),
                new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", user)) });
            var r = await auth.CheckAsync(View(user));
            return r.Allowed == false;   // a - a = deny, even when viewer holds
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public void Wildcard_grants_every_user()
    {
        AlgebraGenerators.UserId.SampleAsync(async user =>
        {
            var auth = await NewAsync(AlgebraGenerators.MonotoneSchema(),
                new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "*")) });
            var r = await auth.CheckAsync(View(user));
            return r.Allowed == true;
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public void Nested_group_chain_is_reachable()
    {
        Gen.Select(AlgebraGenerators.ChainLength, AlgebraGenerators.UserId)
            .SampleAsync(async pair =>
            {
                var (length, user) = pair;
                var tuples = AlgebraGenerators.NestedChainTuples(length, user);
                var auth = await NewAsync(AlgebraGenerators.MonotoneSchema(), tuples);
                var r = await auth.CheckAsync(View(user));
                return r.Allowed == true;   // user at the bottom of the chain reaches the top grant
            }).GetAwaiter().GetResult();
    }

    [Fact]
    public void Union_is_monotone_over_exclusion_free_permission()
    {
        // Granting viewer to a subject can only flip deny->allow on an exclusion-free permission.
        AlgebraGenerators.UserId.SampleAsync(async user =>
        {
            var without = await NewAsync(AlgebraGenerators.MonotoneSchema(), Array.Empty<RelationTuple>());
            var before = (await without.CheckAsync(View(user))).Allowed;

            var with = await NewAsync(AlgebraGenerators.MonotoneSchema(),
                new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", user)) });
            var after = (await with.CheckAsync(View(user))).Allowed;

            // monotone: before implies after, and after must be true once granted.
            return (!before || after) && after;
        }).GetAwaiter().GetResult();
    }
}
```

- [ ] **Step 2: Run to verify (expect PASS)**

Run: `dotnet test tests/Relkit.Conformance --filter AlgebraInvariantTests`
Expected: PASS (4 properties). A CsCheck counterexample here is a genuine engine bug — minimize it and fix `m0/05`.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Conformance/Properties
git commit -m "test: add CsCheck algebra invariant properties over engine-driven authorizer"
```

---

### Task 6: Suite aggregation and CI entry

**Files:**
- Create: `tests/Relkit.Conformance/ConformanceSuite.cs`
- Test: `tests/Relkit.Conformance/SuiteCoverageTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyList<ConformanceCase> ConformanceSuite.All()` aggregating every named worked-example case, and a coverage test asserting all six §12 examples are represented (by name prefix `12.1`–`12.6`).
- Consumes: `WorkedExamples.*`, `ConformanceRunner`.

> This makes the suite addressable as a single list (the bar a future storage provider must pass) and guards that no worked example is dropped.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Conformance/SuiteCoverageTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Conformance;

public class SuiteCoverageTests
{
    [Fact]
    public void Suite_covers_all_six_worked_examples()
    {
        var names = ConformanceSuite.All().Select(c => c.Name).ToList();
        foreach (var prefix in new[] { "12.1", "12.2", "12.3", "12.4", "12.5", "12.6" })
            names.ShouldContain(n => n.StartsWith(prefix), $"no conformance case for worked example {prefix}");
    }

    [Fact]
    public async Task Every_case_in_the_suite_holds()
    {
        foreach (var c in ConformanceSuite.All())
            await ConformanceRunner.AssertAsync(c);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Conformance --filter SuiteCoverageTests`
Expected: FAIL — `ConformanceSuite` not defined.

- [ ] **Step 3: Implement the aggregator**

```csharp
// tests/Relkit.Conformance/ConformanceSuite.cs
namespace Relkit.Conformance;

public static class ConformanceSuite
{
    public static IReadOnlyList<ConformanceCase> All() =>
    [
        WorkedExamples.RoleGrantOverCategory(),               // 12.1
        WorkedExamples.TeamGrantOverCuratedSet(),             // 12.2
        WorkedExamples.SiteScopedAccess(),                    // 12.3
        WorkedExamples.SiteScopedAccessOtherSiteDenied(),     // 12.3 (negative)
        WorkedExamples.DirectGrantOnInstance(),               // 12.4
        WorkedExamples.QuarantineTrainedVetAllowed(),         // 12.5
        WorkedExamples.QuarantineUntrainedVetDenied(),        // 12.5 (negative)
        WorkedExamples.OutsideQuarantineBaseAccess(),         // 12.5 (passthrough)
        WorkedExamples.TimeBoundedDispenseWithinHours(),      // 12.6
    ];
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Conformance --filter SuiteCoverageTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the whole conformance project**

Run: `dotnet test tests/Relkit.Conformance`
Expected: PASS (all case-format, runner, worked-example, gate, property, and coverage tests).

- [ ] **Step 6: Commit**

```bash
git add tests/Relkit.Conformance/ConformanceSuite.cs tests/Relkit.Conformance/SuiteCoverageTests.cs
git commit -m "test: aggregate conformance suite and assert six-example coverage"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`; `dotnet test tests/Relkit.Conformance` green.
- [ ] The case format carries schema + tuples + attributes + (subject,object,permission,context) → expected (Task 1).
- [ ] The runner routes through `m0/03` validation so malformed worked-example schemas fail loudly (Task 2).
- [ ] All six §12 worked examples are encoded as named cases, each with a complete, validation-passing schema where every arrow target exists as a permission/relation (Tasks 3–4).
- [ ] 12.5 has all three discriminating outcomes (trained-allowed, untrained-denied, outside-passthrough) (Task 4).
- [ ] Property scoping is correct: monotonicity is restricted to exclusion/condition-free permissions; `a-a=deny`, wildcard, and nesting hold generally (Task 5).
- [ ] `ConformanceSuite.All()` covers 12.1–12.6 and every case passes (Task 6).

## Contract gaps (reported, not changed)

- **`m0/03` validator entry point — confirmed, no gap.** This plan calls the static `SchemaValidator.Validate(schema) -> SchemaValidationResult` from `m0/03` (verified against the written `m0/03` plan). `EmptyConditionBody` (from `m0/02`) passes `m0/03` validation because body type-checking is deferred to `m0/06`, so the 12.6 `within_hours` case validates under M0. No `README.md` change made; no outstanding assumption.
- **12.6 deny-twin deferred to `m0/06`.** Under the M0 `NullConditionEvaluator` the out-of-hours deny cannot be asserted (conditions are transparently true), so the in-hours allow is encoded now and the out-of-hours deny twin is authored as a note for `m0/06` (which provides the real `within_hours` evaluator and the runner overload that injects it). No contract change; this is a milestone-sequencing note.
