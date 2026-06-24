# M0/02 — Schema Model & Fluent Builder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up `Relkit.Core` and a fluent `SchemaBuilder` that produces the `Schema` AST defined in `Relkit.Abstractions`.

**Architecture:** The AST records live in `Relkit.Abstractions` (built in `m0/01`). The builder is construction logic and lives in `Relkit.Core`. Default chaining of permission terms is **union**; `Exclude`/`Intersect`/`Conditioned` wrap the accumulated expression.

**Tech Stack:** .NET 10, C# 14, xUnit, Shouldly.

## Global Constraints

See `../README.md` → Global Constraints. Depends on `m0/01` (the AST records and `Relkit.Abstractions`).

---

### Task 1: Create `Relkit.Core` and reference Abstractions

**Files:**
- Create: `src/Relkit.Core/Relkit.Core.csproj`
- Create: `tests/Relkit.Core.Tests/Relkit.Core.Tests.csproj`
- Test: `tests/Relkit.Core.Tests/CoreWiringTests.cs`

**Interfaces:**
- Produces: the `Relkit.Core` assembly referencing `Relkit.Abstractions`.

- [ ] **Step 1: Create projects and references**

Run:
```bash
dotnet new classlib -n Relkit.Core -o src/Relkit.Core -f net10.0
dotnet new xunit -n Relkit.Core.Tests -o tests/Relkit.Core.Tests -f net10.0
rm src/Relkit.Core/Class1.cs tests/Relkit.Core.Tests/UnitTest1.cs
dotnet sln add src/Relkit.Core tests/Relkit.Core.Tests
dotnet add src/Relkit.Core reference src/Relkit.Abstractions
dotnet add tests/Relkit.Core.Tests reference src/Relkit.Core
dotnet add tests/Relkit.Core.Tests package Shouldly
```

- [ ] **Step 2: Write the wiring test**

```csharp
// tests/Relkit.Core.Tests/CoreWiringTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class CoreWiringTests
{
    [Fact]
    public void Core_references_abstractions()
    {
        typeof(Relkit.Core.SchemaBuilder).Assembly.GetName().Name.ShouldBe("Relkit.Core");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests`
Expected: FAIL — `SchemaBuilder` does not exist.

- [ ] **Step 4: Add a placeholder `SchemaBuilder`** (filled in Task 2–4)

```csharp
// src/Relkit.Core/SchemaBuilder.cs
namespace Relkit.Core;
public sealed partial class SchemaBuilder { }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "chore: scaffold Relkit.Core project"
```

---

### Task 2: Permission expression builder

**Files:**
- Create: `src/Relkit.Core/PermExprBuilder.cs`
- Test: `tests/Relkit.Core.Tests/PermExprBuilderTests.cs`

**Interfaces:**
- Produces: `PermExprBuilder` with `Relation(string)`, `Arrow(string,string)`, `Union(Action<PermExprBuilder>)`, `Intersect(Action<PermExprBuilder>)`, `Exclude(Action<PermExprBuilder>)`, `Conditioned(string)`, and `PermExpr Build()`.
- Consumes: `PermExpr`, `RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned` from `Relkit.Abstractions`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/PermExprBuilderTests.cs
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class PermExprBuilderTests
{
    [Fact]
    public void Chained_terms_union_then_exclude_wraps_accumulated()
    {
        var expr = new PermExprBuilder()
            .Relation("medicator")
            .Arrow("enclosure", "edit")
            .Exclude(x => x.Relation("blocked"))
            .Build();

        // Expect: Exclude(Union(RelationRef medicator, Arrow enclosure->edit), RelationRef blocked)
        var exclude = expr.ShouldBeOfType<Exclude>();
        exclude.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("blocked");
        var union = exclude.Left.ShouldBeOfType<Union>();
        union.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("medicator");
        var arrow = union.Right.ShouldBeOfType<Arrow>();
        arrow.Relation.ShouldBe("enclosure");
        arrow.Permission.ShouldBe("edit");
    }

    [Fact]
    public void Intersect_and_conditioned_wrap_in_order()
    {
        var expr = new PermExprBuilder()
            .Relation("a")
            .Intersect(x => x.Relation("b"))
            .Conditioned("within_hours")
            .Build();

        var cond = expr.ShouldBeOfType<Conditioned>();
        cond.ConditionName.ShouldBe("within_hours");
        var inter = cond.Inner.ShouldBeOfType<Intersect>();
        inter.Left.ShouldBeOfType<RelationRef>().Relation.ShouldBe("a");
        inter.Right.ShouldBeOfType<RelationRef>().Relation.ShouldBe("b");
    }

    [Fact]
    public void Build_with_no_terms_throws()
    {
        Should.Throw<InvalidOperationException>(() => new PermExprBuilder().Build());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter PermExprBuilderTests`
Expected: FAIL — `PermExprBuilder` not defined.

- [ ] **Step 3: Implement the builder**

```csharp
// src/Relkit.Core/PermExprBuilder.cs
using Relkit.Abstractions;

namespace Relkit.Core;

public sealed class PermExprBuilder
{
    private PermExpr? _current;

    private PermExprBuilder Add(PermExpr node)
    {
        _current = _current is null ? node : new Union(_current, node);
        return this;
    }

    public PermExprBuilder Relation(string relation) => Add(new RelationRef(relation));
    public PermExprBuilder Arrow(string relation, string permission) => Add(new Arrow(relation, permission));

    public PermExprBuilder Union(Action<PermExprBuilder> build) => Add(BuildSub(build));

    public PermExprBuilder Intersect(Action<PermExprBuilder> build)
    {
        _current = new Intersect(Require(), BuildSub(build));
        return this;
    }

    public PermExprBuilder Exclude(Action<PermExprBuilder> build)
    {
        _current = new Exclude(Require(), BuildSub(build));
        return this;
    }

    public PermExprBuilder Conditioned(string conditionName)
    {
        _current = new Conditioned(Require(), conditionName);
        return this;
    }

    public PermExpr Build() => Require();

    private PermExpr Require() => _current ?? throw new InvalidOperationException("Permission expression has no terms.");

    private static PermExpr BuildSub(Action<PermExprBuilder> build)
    {
        var sub = new PermExprBuilder();
        build(sub);
        return sub.Build();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter PermExprBuilderTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: add permission expression builder"
```

---

### Task 3: Relation-filler and condition-param builders

**Files:**
- Create: `src/Relkit.Core/SubjectFillerBuilder.cs`
- Create: `src/Relkit.Core/ConditionParamBuilder.cs`
- Test: `tests/Relkit.Core.Tests/FillerAndParamBuilderTests.cs`

**Interfaces:**
- Produces: `SubjectFillerBuilder` with `User()`, `Type(string)`, `SubjectSet(string type, string relation)`, `Wildcard(string type)`, `IReadOnlyList<SubjectTypeRef> Build()`; `ConditionParamBuilder` with `Bool/Int/Long/Double/String/Timestamp(string name)` and `IReadOnlyList<ConditionParam> Build()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/FillerAndParamBuilderTests.cs
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class FillerAndParamBuilderTests
{
    [Fact]
    public void Filler_builder_collects_user_subjectset_and_wildcard()
    {
        var fillers = new SubjectFillerBuilder().User().SubjectSet("group", "member").Wildcard("user").Build();
        fillers.ShouldContain(new SubjectTypeRef("user", null, false));
        fillers.ShouldContain(new SubjectTypeRef("group", "member", false));
        fillers.ShouldContain(new SubjectTypeRef("user", null, true));
    }

    [Fact]
    public void Param_builder_collects_typed_params()
    {
        var ps = new ConditionParamBuilder().Int("start").Int("end").Build();
        ps.ShouldBe(new[] { new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int) });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter FillerAndParamBuilderTests`
Expected: FAIL — builders not defined.

- [ ] **Step 3: Implement the builders**

```csharp
// src/Relkit.Core/SubjectFillerBuilder.cs
using Relkit.Abstractions;

namespace Relkit.Core;

public sealed class SubjectFillerBuilder
{
    private readonly List<SubjectTypeRef> _fillers = [];
    public SubjectFillerBuilder User() { _fillers.Add(new SubjectTypeRef("user")); return this; }
    public SubjectFillerBuilder Type(string type) { _fillers.Add(new SubjectTypeRef(type)); return this; }
    public SubjectFillerBuilder SubjectSet(string type, string relation) { _fillers.Add(new SubjectTypeRef(type, relation)); return this; }
    public SubjectFillerBuilder Wildcard(string type) { _fillers.Add(new SubjectTypeRef(type, null, true)); return this; }
    public IReadOnlyList<SubjectTypeRef> Build() => _fillers;
}
```

```csharp
// src/Relkit.Core/ConditionParamBuilder.cs
using Relkit.Abstractions;

namespace Relkit.Core;

public sealed class ConditionParamBuilder
{
    private readonly List<ConditionParam> _params = [];
    private ConditionParamBuilder Add(string name, ConditionType type) { _params.Add(new ConditionParam(name, type)); return this; }
    public ConditionParamBuilder Bool(string name) => Add(name, ConditionType.Bool);
    public ConditionParamBuilder Int(string name) => Add(name, ConditionType.Int);
    public ConditionParamBuilder Long(string name) => Add(name, ConditionType.Long);
    public ConditionParamBuilder Double(string name) => Add(name, ConditionType.Double);
    public ConditionParamBuilder String(string name) => Add(name, ConditionType.String);
    public ConditionParamBuilder Timestamp(string name) => Add(name, ConditionType.Timestamp);
    public IReadOnlyList<ConditionParam> Build() => _params;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter FillerAndParamBuilderTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: add subject-filler and condition-param builders"
```

---

### Task 4: Type and schema builders

**Files:**
- Create: `src/Relkit.Core/EntityTypeBuilder.cs`
- Modify: `src/Relkit.Core/SchemaBuilder.cs`
- Test: `tests/Relkit.Core.Tests/SchemaBuilderTests.cs`

**Interfaces:**
- Produces: `EntityTypeBuilder` with `Relation(string name, Action<SubjectFillerBuilder>)` and `Permission(string name, Action<PermExprBuilder>)`; `SchemaBuilder(string version)` with `Type(string name, Action<EntityTypeBuilder>)`, `Condition(string name, Action<ConditionParamBuilder>)`, and `Schema Build()`. (Condition bodies are attached in `m0/06`; here a condition is declared with params and an empty body marker.)
- Consumes: all builders above; `Schema`, `EntityTypeDef`, `RelationDef`, `PermissionDef`, `ConditionDef`, `ConditionExpr`.

- [ ] **Step 1: Write the failing test** (builds the animal schema from the contract)

```csharp
// tests/Relkit.Core.Tests/SchemaBuilderTests.cs
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class SchemaBuilderTests
{
    [Fact]
    public void Builds_animal_schema_with_relations_permission_and_condition()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("animal", t => t
                .Relation("medicator", s => s.User().SubjectSet("group", "member"))
                .Relation("enclosure", s => s.Type("enclosure"))
                .Relation("blocked", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("medicator").Arrow("enclosure", "edit").Exclude(x => x.Relation("blocked"))))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();

        schema.Version.ShouldBe("v1");
        var animal = schema.Types.Single(x => x.Name == "animal");
        animal.Relations.Select(r => r.Name).ShouldBe(new[] { "medicator", "enclosure", "blocked" });
        animal.Permissions.Single().Name.ShouldBe("edit");
        animal.Permissions.Single().Expression.ShouldBeOfType<Exclude>();
        schema.Conditions.Single().Name.ShouldBe("within_hours");
        schema.Conditions.Single().Parameters.Count.ShouldBe(2);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter SchemaBuilderTests`
Expected: FAIL — `Type`/`Permission`/`Condition` not defined.

- [ ] **Step 3: Implement the type builder**

```csharp
// src/Relkit.Core/EntityTypeBuilder.cs
using Relkit.Abstractions;

namespace Relkit.Core;

public sealed class EntityTypeBuilder
{
    private readonly List<RelationDef> _relations = [];
    private readonly List<PermissionDef> _permissions = [];

    public EntityTypeBuilder Relation(string name, Action<SubjectFillerBuilder> fillers)
    {
        var b = new SubjectFillerBuilder();
        fillers(b);
        _relations.Add(new RelationDef(name, b.Build()));
        return this;
    }

    public EntityTypeBuilder Permission(string name, Action<PermExprBuilder> expr)
    {
        var b = new PermExprBuilder();
        expr(b);
        _permissions.Add(new PermissionDef(name, b.Build()));
        return this;
    }

    internal (IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions) Build() => (_relations, _permissions);
}
```

- [ ] **Step 4: Implement the schema builder**

```csharp
// src/Relkit.Core/SchemaBuilder.cs
using Relkit.Abstractions;

namespace Relkit.Core;

/// <summary>Empty condition body placeholder; m0/06 replaces this with the real body AST.</summary>
public sealed record EmptyConditionBody : ConditionExpr;

public sealed partial class SchemaBuilder
{
    private readonly string _version;
    private readonly List<EntityTypeDef> _types = [];
    private readonly List<ConditionDef> _conditions = [];

    public SchemaBuilder(string version) => _version = version;

    public SchemaBuilder Type(string name, Action<EntityTypeBuilder> build)
    {
        var b = new EntityTypeBuilder();
        build(b);
        var (relations, permissions) = b.Build();
        _types.Add(new EntityTypeDef(name, relations, permissions));
        return this;
    }

    public SchemaBuilder Condition(string name, Action<ConditionParamBuilder> build)
    {
        var b = new ConditionParamBuilder();
        build(b);
        _conditions.Add(new ConditionDef(name, b.Build(), new EmptyConditionBody()));
        return this;
    }

    public Schema Build() => new(_version, _types, _conditions);
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter SchemaBuilderTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: add entity-type and schema builders"
```

---

## Self-review checklist (run after all tasks)

- [ ] The contract's fluent example in `../README.md` compiles and produces the expected AST.
- [ ] Default chaining is union; `Exclude`/`Intersect`/`Conditioned` wrap the accumulated expression.
- [ ] `m0/06` will replace `EmptyConditionBody`; leave a clear note (done) so the condition-evaluator plan knows to attach real bodies via a `Condition(name, params, bodyExpr)` overload.
