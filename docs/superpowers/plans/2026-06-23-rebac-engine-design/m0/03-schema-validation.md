# M0/03 — Schema Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate a `Schema` before it can be made active: every `RelationRef`/`Arrow` resolves to a declared relation or permission, every arrow target permission exists on the related type, permission recursion terminates (no infinite arrow/permission cycle), every `Conditioned` branch and condition-carrying tuple references a declared `ConditionDef`, and condition **parameter values** type-check against the declared `ConditionParam` types. Produce `SchemaValidator` in `Relkit.Core` returning `SchemaValidationResult`; wire `SchemaManager.ValidateSchema` to it; make `SetActiveSchemaAsync` throw `SchemaValidationException` on an invalid schema.

**Architecture:** `SchemaValidator` is a pure function `Schema -> SchemaValidationResult` in `Relkit.Core`. It resolves names against the schema, derives arrow targets from each relation's `AllowedSubjects`, and proves recursion termination with a static cycle check over a permission-dependency graph. `SchemaManager` is the `ISchemaManager` implementation in `Relkit.Core`; it delegates validation to `SchemaValidator` and persists through `ISchemaStore`. This is a **static, schema-time** check; it is distinct from the **runtime data-cycle guard** in `m0/05`, which guards tuple graphs, not the schema.

**Condition-body seam (read before starting):** the concrete `ConditionExpr` body AST does not exist yet — `m0/01` left an abstract `ConditionExpr` marker and `m0/02` attaches `EmptyConditionBody`. The condition **body** type-checker is owned by `m0/06`, which adds the concrete nodes and *extends* `SchemaValidator` to type-check bodies. This plan therefore checks only what is possible without the body AST: that a `Conditioned` branch names a declared `ConditionDef`, and that a `ConditionRef`'s **parameter values** match the declared `ConditionParam` types (an `Int` param requires an `int`/`long` value, etc.). Do not define concrete body nodes here.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. Depends on `m0/01` (`Relkit.Abstractions`) and `m0/02` (`Relkit.Core`, `SchemaBuilder`). Uses the canonical contract type names verbatim.

---

### Task 1: Relation and permission resolution

**Files:**
- Create: `src/Relkit.Core/Validation/SchemaValidator.cs`
- Test: `tests/Relkit.Core.Tests/Validation/RelationResolutionTests.cs`

**Interfaces:**
- Produces: `SchemaValidator.Validate(Schema) -> SchemaValidationResult`.
- Consumes: `Schema`, `EntityTypeDef`, `RelationDef`, `PermissionDef`, `PermExpr` (`RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned`), `SchemaValidationResult` from `Relkit.Abstractions`.

A `RelationRef(name)` is valid when `name` is **either** a declared relation **or** a declared permission on the *same* type (permission nesting, e.g. `manage ⊃ edit`). There is no separate `PermissionRef` node, so do not reject a `RelationRef` that names a permission.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/Validation/RelationResolutionTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Validation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Validation;

public class RelationResolutionTests
{
    [Fact]
    public void Valid_schema_with_relation_and_nested_permission_passes()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User())
                .Permission("edit", p => p.Relation("medicator"))
                .Permission("manage", p => p.Relation("edit")))   // RelationRef naming a permission (nesting)
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void RelationRef_to_unknown_name_fails_with_message()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User())
                .Permission("edit", p => p.Relation("ghost")))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("animal") && e.Contains("ghost"));
    }

    [Fact]
    public void Duplicate_relation_name_on_a_type_fails()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [new RelationDef("medicator", [new SubjectTypeRef("user")]),
                 new RelationDef("medicator", [new SubjectTypeRef("user")])],
                [])],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("medicator") && e.Contains("duplicate"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.RelationResolutionTests`
Expected: FAIL — `SchemaValidator` does not exist.

- [ ] **Step 3: Implement the resolver core**

```csharp
// src/Relkit.Core/Validation/SchemaValidator.cs
using Relkit.Abstractions;

namespace Relkit.Core.Validation;

public static class SchemaValidator
{
    public static SchemaValidationResult Validate(Schema schema)
    {
        var errors = new List<string>();
        var types = IndexTypes(schema, errors);
        var conditions = IndexConditions(schema, errors);

        foreach (var type in schema.Types)
        {
            foreach (var perm in type.Permissions)
                ValidateExpr(type, perm.Name, perm.Expression, types, conditions, errors);
        }

        return new SchemaValidationResult(errors.Count == 0, errors);
    }

    private static Dictionary<string, EntityTypeDef> IndexTypes(Schema schema, List<string> errors)
    {
        var types = new Dictionary<string, EntityTypeDef>(StringComparer.Ordinal);
        foreach (var type in schema.Types)
        {
            if (!types.TryAdd(type.Name, type))
                errors.Add($"Duplicate entity type '{type.Name}'.");

            var relNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in type.Relations)
                if (!relNames.Add(rel.Name))
                    errors.Add($"Type '{type.Name}' declares duplicate relation '{rel.Name}'.");

            var permNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var perm in type.Permissions)
                if (!permNames.Add(perm.Name))
                    errors.Add($"Type '{type.Name}' declares duplicate permission '{perm.Name}'.");
        }
        return types;
    }

    private static Dictionary<string, ConditionDef> IndexConditions(Schema schema, List<string> errors)
    {
        var conditions = new Dictionary<string, ConditionDef>(StringComparer.Ordinal);
        foreach (var cond in schema.Conditions)
            if (!conditions.TryAdd(cond.Name, cond))
                errors.Add($"Duplicate condition '{cond.Name}'.");
        return conditions;
    }

    private static bool HasRelation(EntityTypeDef type, string name) =>
        type.Relations.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    private static bool HasPermission(EntityTypeDef type, string name) =>
        type.Permissions.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static void ValidateExpr(
        EntityTypeDef type, string permission, PermExpr expr,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        IReadOnlyDictionary<string, ConditionDef> conditions,
        List<string> errors)
    {
        switch (expr)
        {
            case RelationRef r:
                if (!HasRelation(type, r.Relation) && !HasPermission(type, r.Relation))
                    errors.Add($"Permission '{type.Name}.{permission}' references '{r.Relation}', " +
                               $"which is neither a relation nor a permission on '{type.Name}'.");
                break;

            case Union u:
                ValidateExpr(type, permission, u.Left, types, conditions, errors);
                ValidateExpr(type, permission, u.Right, types, conditions, errors);
                break;

            case Intersect i:
                ValidateExpr(type, permission, i.Left, types, conditions, errors);
                ValidateExpr(type, permission, i.Right, types, conditions, errors);
                break;

            case Exclude e:
                ValidateExpr(type, permission, e.Left, types, conditions, errors);
                ValidateExpr(type, permission, e.Right, types, conditions, errors);
                break;

            case Arrow a:
                ValidateArrow(type, permission, a, types, errors);
                break;

            case Conditioned c:
                ValidateExpr(type, permission, c.Inner, types, conditions, errors);
                if (!conditions.ContainsKey(c.ConditionName))
                    errors.Add($"Permission '{type.Name}.{permission}' references condition " +
                               $"'{c.ConditionName}', which is not declared.");
                break;
        }
    }

    private static void ValidateArrow(
        EntityTypeDef type, string permission, Arrow arrow,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        var relation = type.Relations.FirstOrDefault(r =>
            string.Equals(r.Name, arrow.Relation, StringComparison.Ordinal));
        if (relation is null)
        {
            errors.Add($"Permission '{type.Name}.{permission}' arrows through relation " +
                       $"'{arrow.Relation}', which is not declared on '{type.Name}'.");
            return;
        }

        // Placeholder; full arrow-target resolution is added in Task 2.
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.RelationResolutionTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: validate relation and permission resolution in schemas"
```

---

### Task 2: Arrow target resolution

**Files:**
- Modify: `src/Relkit.Core/Validation/SchemaValidator.cs`
- Test: `tests/Relkit.Core.Tests/Validation/ArrowResolutionTests.cs`

**Interfaces:**
- Produces: arrow-target checking inside `SchemaValidator.Validate`.
- Consumes: `Arrow`, `RelationDef`, `SubjectTypeRef`.

An `Arrow(relation, permission)` is valid when, for **every** non-wildcard object type that may fill `relation` (drawn from the relation's `AllowedSubjects`), that type declares the named `permission`. The arrow target types are exactly the `SubjectTypeRef.Type` values on the relation; a subject-set filler (`SubjectTypeRef.Relation is not null`) names a relation on the related type, not a permission, and is not a valid arrow target. Pin the multi-target case where one target type is missing the permission.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/Validation/ArrowResolutionTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Validation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Validation;

public class ArrowResolutionTests
{
    [Fact]
    public void Arrow_to_permission_present_on_related_type_passes()
    {
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("can_edit", s => s.User())
                .Permission("edit", p => p.Relation("can_edit")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Arrow_to_permission_absent_on_related_type_fails()
    {
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t.Relation("can_edit", s => s.User()))   // no 'edit' permission
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("enclosure") && e.Contains("edit"));
    }

    [Fact]
    public void Arrow_fails_when_one_of_several_target_types_lacks_the_permission()
    {
        // 'parent' may be an enclosure (has edit) or a site (lacks edit).
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("can_edit", s => s.User())
                .Permission("edit", p => p.Relation("can_edit")))
            .Type("site", t => t.Relation("can_edit", s => s.User()))   // no 'edit' permission
            .Type("animal", t => t
                .Relation("parent", s => s.Type("enclosure").Type("site"))
                .Permission("edit", p => p.Arrow("parent", "edit")))
            .Build();

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("site") && e.Contains("edit"));
    }

    [Fact]
    public void Arrow_to_unknown_related_type_fails()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [new RelationDef("enclosure", [new SubjectTypeRef("enclosure")])],
                [new PermissionDef("edit", new Arrow("enclosure", "edit"))])],
            []);   // no 'enclosure' type declared at all

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("enclosure"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.ArrowResolutionTests`
Expected: FAIL — the placeholder `ValidateArrow` does not yet check targets.

- [ ] **Step 3: Replace the placeholder with full arrow-target resolution**

```csharp
// src/Relkit.Core/Validation/SchemaValidator.cs  (replace the ValidateArrow method body)
    private static void ValidateArrow(
        EntityTypeDef type, string permission, Arrow arrow,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        var relation = type.Relations.FirstOrDefault(r =>
            string.Equals(r.Name, arrow.Relation, StringComparison.Ordinal));
        if (relation is null)
        {
            errors.Add($"Permission '{type.Name}.{permission}' arrows through relation " +
                       $"'{arrow.Relation}', which is not declared on '{type.Name}'.");
            return;
        }

        foreach (var filler in relation.AllowedSubjects)
        {
            // Subject-set fillers (e.g. group#member) name a relation, not an arrow target.
            if (filler.Relation is not null)
                continue;

            if (!types.TryGetValue(filler.Type, out var target))
            {
                errors.Add($"Permission '{type.Name}.{permission}' arrows into type " +
                           $"'{filler.Type}', which is not declared.");
                continue;
            }

            if (!HasPermission(target, arrow.Permission))
                errors.Add($"Permission '{type.Name}.{permission}' arrows to " +
                           $"'{filler.Type}.{arrow.Permission}', which is not a permission on '{filler.Type}'.");
        }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.ArrowResolutionTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: validate arrow targets against related-type permissions"
```

---

### Task 3: Permission recursion termination

**Files:**
- Modify: `src/Relkit.Core/Validation/SchemaValidator.cs`
- Test: `tests/Relkit.Core.Tests/Validation/RecursionTerminationTests.cs`

**Interfaces:**
- Produces: a static permission-cycle check inside `SchemaValidator.Validate`.

Termination is proved by a DFS over a **permission-dependency graph**. Each node is a `(type, permission)` pair. A permission's expression contributes edges:

- a `RelationRef(name)` where `name` is a **permission** on the same type ⇒ edge to `(thisType, name)` (a `RelationRef` naming a *relation* contributes no edge);
- an `Arrow(relation, perm)` ⇒ an edge to `(U, perm)` for every non-wildcard, non-subject-set target type `U` of `relation`.

A back-edge during DFS (grey node revisited) is a non-terminating cycle and is invalid. This is purely structural: a subject-set self-reference such as `member: group#member` is a relation filler, not a permission edge, and must not be flagged. Run cycle detection only when name/arrow resolution already succeeded, so an unresolved name does not masquerade as a cycle.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/Validation/RecursionTerminationTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Validation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Validation;

public class RecursionTerminationTests
{
    [Fact]
    public void Self_referential_permission_is_a_cycle()
    {
        var schema = new Schema("v1",
            [new EntityTypeDef("animal",
                [],
                [new PermissionDef("edit", new RelationRef("edit"))])],   // edit -> edit
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle") && e.Contains("animal.edit"));
    }

    [Fact]
    public void Permission_cycle_through_an_arrow_is_detected()
    {
        // animal.edit -> enclosure.edit -> animal.edit  (via back-arrows)
        var schema = new Schema("v1",
            [
                new EntityTypeDef("animal",
                    [new RelationDef("enclosure", [new SubjectTypeRef("enclosure")])],
                    [new PermissionDef("edit", new Arrow("enclosure", "edit"))]),
                new EntityTypeDef("enclosure",
                    [new RelationDef("home_animal", [new SubjectTypeRef("animal")])],
                    [new PermissionDef("edit", new Arrow("home_animal", "edit"))]),
            ],
            []);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle"));
    }

    [Fact]
    public void Nested_permissions_without_a_cycle_terminate()
    {
        var schema = new SchemaBuilder("v1")
            .Type("animal", t => t
                .Relation("medicator", s => s.User())
                .Permission("edit", p => p.Relation("medicator"))
                .Permission("manage", p => p.Relation("edit")))   // manage -> edit -> medicator (relation, stops)
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Subject_set_self_reference_is_not_a_permission_cycle()
    {
        // group.member fills with group#member (nesting) — a relation filler, not a permission edge.
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t
                .Relation("member", s => s.User().SubjectSet("group", "member"))
                .Permission("read", p => p.Relation("member")))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.RecursionTerminationTests`
Expected: FAIL — no cycle detection yet (the two cyclic schemas report `IsValid = true`).

- [ ] **Step 3: Add the cycle check**

```csharp
// src/Relkit.Core/Validation/SchemaValidator.cs  (add to the Validate method, after the per-permission loop)
        // Only run cycle detection when resolution succeeded; otherwise an unresolved
        // name would be mistaken for a cycle.
        if (errors.Count == 0)
            DetectCycles(schema, types, errors);

        return new SchemaValidationResult(errors.Count == 0, errors);
```

```csharp
// src/Relkit.Core/Validation/SchemaValidator.cs  (add these members to the class)
    private enum Mark { White, Grey, Black }

    private static void DetectCycles(
        Schema schema,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        var marks = new Dictionary<(string Type, string Perm), Mark>();
        foreach (var type in schema.Types)
            foreach (var perm in type.Permissions)
                marks[(type.Name, perm.Name)] = Mark.White;

        foreach (var node in marks.Keys.ToList())
            if (marks[node] == Mark.White)
                Visit(node, marks, types, errors);
    }

    private static bool Visit(
        (string Type, string Perm) node,
        Dictionary<(string Type, string Perm), Mark> marks,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        marks[node] = Mark.Grey;
        foreach (var next in Edges(node, types))
        {
            if (!marks.TryGetValue(next, out var mark))
                continue;   // edge to a non-permission node; resolution already covered it
            if (mark == Mark.Grey)
            {
                errors.Add($"Permission '{node.Type}.{node.Perm}' is part of a " +
                           $"non-terminating permission cycle.");
                marks[node] = Mark.Black;
                return true;
            }
            if (mark == Mark.White && Visit(next, marks, types, errors))
            {
                marks[node] = Mark.Black;
                return true;
            }
        }
        marks[node] = Mark.Black;
        return false;
    }

    private static IEnumerable<(string Type, string Perm)> Edges(
        (string Type, string Perm) node,
        IReadOnlyDictionary<string, EntityTypeDef> types)
    {
        var type = types[node.Type];
        var perm = type.Permissions.First(p => string.Equals(p.Name, node.Perm, StringComparison.Ordinal));
        foreach (var edge in ExprEdges(type, perm.Expression, types))
            yield return edge;
    }

    private static IEnumerable<(string Type, string Perm)> ExprEdges(
        EntityTypeDef type, PermExpr expr,
        IReadOnlyDictionary<string, EntityTypeDef> types)
    {
        switch (expr)
        {
            case RelationRef r when HasPermission(type, r.Relation):
                yield return (type.Name, r.Relation);
                break;
            case RelationRef:
                break;   // names a relation; not a permission edge
            case Union u:
                foreach (var e in ExprEdges(type, u.Left, types)) yield return e;
                foreach (var e in ExprEdges(type, u.Right, types)) yield return e;
                break;
            case Intersect i:
                foreach (var e in ExprEdges(type, i.Left, types)) yield return e;
                foreach (var e in ExprEdges(type, i.Right, types)) yield return e;
                break;
            case Exclude x:
                foreach (var e in ExprEdges(type, x.Left, types)) yield return e;
                foreach (var e in ExprEdges(type, x.Right, types)) yield return e;
                break;
            case Conditioned c:
                foreach (var e in ExprEdges(type, c.Inner, types)) yield return e;
                break;
            case Arrow a:
                var relation = type.Relations.First(r =>
                    string.Equals(r.Name, a.Relation, StringComparison.Ordinal));
                foreach (var filler in relation.AllowedSubjects)
                {
                    if (filler.Relation is not null) continue;
                    if (types.TryGetValue(filler.Type, out var target) && HasPermission(target, a.Permission))
                        yield return (filler.Type, a.Permission);
                }
                break;
        }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.RecursionTerminationTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: detect non-terminating permission cycles in schemas"
```

---

### Task 4: Condition reference and parameter-value type-checking

**Files:**
- Create: `src/Relkit.Core/Validation/ConditionParamChecker.cs`
- Test: `tests/Relkit.Core.Tests/Validation/ConditionParamCheckTests.cs`

**Interfaces:**
- Produces: `ConditionParamChecker.Check(ConditionDef, IReadOnlyDictionary<string, object?> parameters) -> IReadOnlyList<string>` (the per-parameter errors; empty when the supplied values satisfy the declared `ConditionParam` types).
- Consumes: `ConditionDef`, `ConditionParam`, `ConditionType`.

This checks the **parameter values** carried by a tuple's `ConditionRef` against the declared `ConditionParam` types. It is the only condition check possible before the body AST exists (the body type-check lands in `m0/06`). Coercion rules: `Int`/`Long` accept `int`/`long`; `Double` accepts `int`/`long`/`double`; `Bool` accepts `bool`; `String` accepts `string`; `Timestamp` accepts `DateTimeOffset`/`DateTime`. Every declared parameter must be present; an unknown parameter name is an error.

This helper is invoked by the relation write-path (`IRelationManager.WriteTuplesAsync`) when a tuple carries a `ConditionRef`, so a tuple cannot be written with a mistyped condition parameter. (See the Contract gaps note in the plan return — no M0 plan currently owns that write-path call site; this plan produces the helper so the call site can be wired when the write-path lands.)

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/Validation/ConditionParamCheckTests.cs
using Relkit.Abstractions;
using Relkit.Core.Validation;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Validation;

public class ConditionParamCheckTests
{
    private static readonly ConditionDef WithinHours = new(
        "within_hours",
        [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)],
        new EmptyConditionBody());

    [Fact]
    public void Matching_int_parameters_have_no_errors()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 });

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Wrong_typed_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = "8", ["end"] = 18 });

        errors.ShouldContain(e => e.Contains("start") && e.Contains("Int"));
    }

    [Fact]
    public void Missing_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = 8 });

        errors.ShouldContain(e => e.Contains("end") && e.Contains("missing"));
    }

    [Fact]
    public void Unknown_parameter_is_reported()
    {
        var errors = ConditionParamChecker.Check(WithinHours,
            new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18, ["extra"] = 1 });

        errors.ShouldContain(e => e.Contains("extra") && e.Contains("not declared"));
    }

    [Fact]
    public void Double_param_accepts_integer_value()
    {
        var def = new ConditionDef("at_least",
            [new ConditionParam("n", ConditionType.Double)], new EmptyConditionBody());

        ConditionParamChecker.Check(def, new Dictionary<string, object?> { ["n"] = 3 }).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.ConditionParamCheckTests`
Expected: FAIL — `ConditionParamChecker` does not exist.

- [ ] **Step 3: Implement the parameter checker**

```csharp
// src/Relkit.Core/Validation/ConditionParamChecker.cs
using Relkit.Abstractions;

namespace Relkit.Core.Validation;

public static class ConditionParamChecker
{
    public static IReadOnlyList<string> Check(
        ConditionDef definition, IReadOnlyDictionary<string, object?> parameters)
    {
        var errors = new List<string>();
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var param in definition.Parameters)
        {
            declared.Add(param.Name);
            if (!parameters.TryGetValue(param.Name, out var value))
            {
                errors.Add($"Condition '{definition.Name}' parameter '{param.Name}' is missing.");
                continue;
            }
            if (!Matches(param.Type, value))
                errors.Add($"Condition '{definition.Name}' parameter '{param.Name}' expects " +
                           $"{param.Type} but got '{Describe(value)}'.");
        }

        foreach (var name in parameters.Keys)
            if (!declared.Contains(name))
                errors.Add($"Condition '{definition.Name}' parameter '{name}' is not declared.");

        return errors;
    }

    private static bool Matches(ConditionType type, object? value) => type switch
    {
        ConditionType.Bool => value is bool,
        ConditionType.Int => value is int or long,
        ConditionType.Long => value is int or long,
        ConditionType.Double => value is int or long or double,
        ConditionType.String => value is string,
        ConditionType.Timestamp => value is DateTimeOffset or DateTime,
        _ => false,
    };

    private static string Describe(object? value) =>
        value is null ? "null" : value.GetType().Name;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter Validation.ConditionParamCheckTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: type-check condition parameter values against declared types"
```

---

### Task 5: `SchemaManager` delegating to the validator

**Files:**
- Create: `src/Relkit.Core/SchemaManager.cs`
- Test: `tests/Relkit.Core.Tests/SchemaManagerTests.cs`

**Interfaces:**
- Produces: `SchemaManager : ISchemaManager` with `ValidateSchema` delegating to `SchemaValidator`, `SetActiveSchemaAsync` throwing `SchemaValidationException` on an invalid schema and otherwise persisting through `ISchemaStore`, and `GetActiveSchemaAsync` reading it back.
- Consumes: `ISchemaManager`, `ISchemaStore`, `IUnitOfWorkFactory`, `IUnitOfWork`, `Schema`, `SchemaValidationResult`, `SchemaValidationException`.

`SchemaManager` depends only on the abstractions `ISchemaStore`/`IUnitOfWorkFactory`. The in-memory implementations land in `m0/04`; this task's tests use tiny inline fakes so this plan does not depend on `m0/04`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Core.Tests/SchemaManagerTests.cs
using Relkit.Abstractions;
using Relkit.Core;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class SchemaManagerTests
{
    private sealed class FakeSchemaStore : ISchemaStore
    {
        private readonly Dictionary<string, Schema> _store = new(StringComparer.Ordinal);
        public Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(store, out var s) ? s : null);
        public Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
        {
            _store[store] = schema;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public bool Committed { get; private set; }
        public Task CommitAsync(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeUowFactory : IUnitOfWorkFactory
    {
        public FakeUow Last { get; private set; } = new();
        public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
        {
            Last = new FakeUow();
            return Task.FromResult<IUnitOfWork>(Last);
        }
    }

    private static Schema ValidSchema() => new SchemaBuilder("v1")
        .Type("animal", t => t.Relation("medicator", s => s.User()).Permission("edit", p => p.Relation("medicator")))
        .Build();

    private static Schema InvalidSchema() => new SchemaBuilder("v1")
        .Type("animal", t => t.Relation("medicator", s => s.User()).Permission("edit", p => p.Relation("ghost")))
        .Build();

    [Fact]
    public void ValidateSchema_returns_validator_result()
    {
        var mgr = new SchemaManager(new FakeSchemaStore(), new FakeUowFactory());

        mgr.ValidateSchema(ValidSchema()).IsValid.ShouldBeTrue();
        mgr.ValidateSchema(InvalidSchema()).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task SetActiveSchemaAsync_persists_and_commits_a_valid_schema()
    {
        var store = new FakeSchemaStore();
        var factory = new FakeUowFactory();
        var mgr = new SchemaManager(store, factory);

        await mgr.SetActiveSchemaAsync("zoo", ValidSchema());

        (await mgr.GetActiveSchemaAsync("zoo"))!.Version.ShouldBe("v1");
        factory.Last.Committed.ShouldBeTrue();
    }

    [Fact]
    public async Task SetActiveSchemaAsync_throws_on_an_invalid_schema()
    {
        var mgr = new SchemaManager(new FakeSchemaStore(), new FakeUowFactory());

        var ex = await Should.ThrowAsync<SchemaValidationException>(
            () => mgr.SetActiveSchemaAsync("zoo", InvalidSchema()));

        ex.Errors.ShouldContain(e => e.Contains("ghost"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Core.Tests --filter SchemaManagerTests`
Expected: FAIL — `SchemaManager` does not exist.

- [ ] **Step 3: Implement `SchemaManager`**

```csharp
// src/Relkit.Core/SchemaManager.cs
using Relkit.Abstractions;
using Relkit.Core.Validation;

namespace Relkit.Core;

public sealed class SchemaManager(ISchemaStore store, IUnitOfWorkFactory uowFactory) : ISchemaManager
{
    private readonly ISchemaStore _store = store;
    private readonly IUnitOfWorkFactory _uowFactory = uowFactory;

    public SchemaValidationResult ValidateSchema(Schema schema) => SchemaValidator.Validate(schema);

    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        var result = SchemaValidator.Validate(schema);
        if (!result.IsValid)
            throw new SchemaValidationException(result.Errors);

        await using var uow = await _uowFactory.BeginAsync(ct);
        await _store.SetActiveAsync(store, schema, uow, ct);
        await uow.CommitAsync(ct);
    }

    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default) =>
        _store.GetActiveAsync(store, ct);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Core.Tests --filter SchemaManagerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Core tests/Relkit.Core.Tests
git commit -m "feat: add SchemaManager delegating validation and persisting active schema"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Every `RelationRef`/`Arrow` resolves; a `RelationRef` may name a same-type permission (nesting) without error.
- [ ] Arrow targets are drawn from the relation's `AllowedSubjects` and checked against each target type's permissions, including the multi-target case.
- [ ] Static permission cycles (direct, through arrows) are rejected; subject-set self-references are not flagged.
- [ ] `Conditioned` branches resolve to a declared `ConditionDef`; `ConditionParamChecker` type-checks tuple parameter values.
- [ ] `SchemaManager.ValidateSchema` delegates to `SchemaValidator`; `SetActiveSchemaAsync` throws `SchemaValidationException` on invalid input and commits a valid schema.
- [ ] No condition **body** AST is introduced here; the body type-check is left to `m0/06`.
