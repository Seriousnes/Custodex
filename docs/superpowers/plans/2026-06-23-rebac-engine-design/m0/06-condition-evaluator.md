# M0/06 — ABAC Condition Evaluator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The ABAC condition layer: concrete `ConditionExpr` body nodes (literals, parameter refs, attribute refs, context refs like `now`/`subject`, comparison/boolean/arithmetic/`in`/date-time operators), a sandboxed typed `ConditionEvaluator` that evaluates a `ConditionDef` body against synced resource attributes, a `RequestContext`, and the tuple's `ConditionRef.Parameters`, and a `SchemaBuilder.Condition(name, params, bodyExpr)` overload that attaches a real body — replacing the `EmptyConditionBody` placeholder from `m0/02`.

**Architecture:** The body AST is a small expression tree under `ConditionExpr` (the abstract marker from `m0/01`), kept **CEL-shaped** so a future move to Google CEL is possible without changing stored schemas: literals, ref nodes (`param`, `resource[field]`, `context.now`/`context.subject`), and operator nodes. `ConditionEvaluator` walks it with a typed value model and three input sources (attributes, context, params). Evaluation is deterministic and sandboxed — no I/O, no reflection over arbitrary types, ambient time only via `RequestContext.Now`. A runtime failure (missing attribute, type mismatch) is **not** an escaping exception: it returns a result the Check/List layers map to *deny + diagnostic* (spec §10.3). The `Condition(name, params, bodyExpr)` overload is added alongside the params-only overload from `m0/02`, which keeps working.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; identifiers are non-empty ordinal strings; **determinism — ambient time enters only through `RequestContext.Now`**, never `DateTime.Now`. Depends on `m0/01` (`Custodex.Abstractions`, the `ConditionExpr` marker), `m0/02` (`SchemaBuilder`, `EmptyConditionBody`), and `m0/03` (`ConditionParamChecker`, reused for body-time param typing). Concrete `ConditionExpr` nodes and the evaluator live in `Custodex.Core` (consistent with `EmptyConditionBody`).

---

### Task 1: Concrete `ConditionExpr` body nodes

**Files:**
- Create: `src/Custodex.Core/Conditions/ConditionExprNodes.cs`
- Test: `tests/Custodex.Core.Tests/Conditions/ConditionExprNodesTests.cs`

**Interfaces:**
- Produces, all deriving from `Custodex.Abstractions.ConditionExpr`:
  - Literals: `LiteralBool(bool)`, `LiteralInt(long)`, `LiteralDouble(double)`, `LiteralString(string)`.
  - Refs: `ParamRef(string Name)`, `AttributeRef(string Field)` (`resource[field]`), `ContextNow` (`context.now`), `ContextSubject` (`context.subject`).
  - Operators: `Compare(ConditionExpr Left, CompareOp Op, ConditionExpr Right)`, `BoolOp(ConditionExpr Left, BoolConnective Op, ConditionExpr Right)`, `Not(ConditionExpr Inner)`, `Arithmetic(ConditionExpr Left, ArithOp Op, ConditionExpr Right)`, `InList(ConditionExpr Item, IReadOnlyList<ConditionExpr> Items)`, `HourOf(ConditionExpr Timestamp)`.
  - Enums: `CompareOp { Eq, Ne, Lt, Le, Gt, Ge }`, `BoolConnective { And, Or }`, `ArithOp { Add, Sub, Mul, Div }`.
- Consumes: `ConditionExpr` from `Custodex.Abstractions`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/Conditions/ConditionExprNodesTests.cs
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class ConditionExprNodesTests
{
    [Fact]
    public void Nodes_derive_from_condition_expr_and_are_pattern_matchable()
    {
        ConditionExpr expr = new Compare(
            new AttributeRef("weight"), CompareOp.Ge, new ParamRef("n"));

        var label = expr switch
        {
            Compare c when c.Op == CompareOp.Ge => "ge",
            _ => "other",
        };
        label.ShouldBe("ge");
        expr.ShouldBeAssignableTo<ConditionExpr>();
    }

    [Fact]
    public void Within_hours_body_is_expressible_as_a_tree()
    {
        // context.now.hour >= start && context.now.hour < end
        ConditionExpr body = new BoolOp(
            new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef("start")),
            BoolConnective.And,
            new Compare(new HourOf(new ContextNow()), CompareOp.Lt, new ParamRef("end")));

        body.ShouldBeOfType<BoolOp>().Op.ShouldBe(BoolConnective.And);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter Conditions.ConditionExprNodesTests`
Expected: FAIL — the node types do not exist.

- [ ] **Step 3: Implement the body nodes**

```csharp
// src/Custodex.Core/Conditions/ConditionExprNodes.cs
using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

public enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge }
public enum BoolConnective { And, Or }
public enum ArithOp { Add, Sub, Mul, Div }

public sealed record LiteralBool(bool Value) : ConditionExpr;
public sealed record LiteralInt(long Value) : ConditionExpr;
public sealed record LiteralDouble(double Value) : ConditionExpr;
public sealed record LiteralString(string Value) : ConditionExpr;

public sealed record ParamRef(string Name) : ConditionExpr;
public sealed record AttributeRef(string Field) : ConditionExpr;      // resource[field]
public sealed record ContextNow : ConditionExpr;                      // context.now
public sealed record ContextSubject : ConditionExpr;                  // context.subject

public sealed record Compare(ConditionExpr Left, CompareOp Op, ConditionExpr Right) : ConditionExpr;
public sealed record BoolOp(ConditionExpr Left, BoolConnective Op, ConditionExpr Right) : ConditionExpr;
public sealed record Not(ConditionExpr Inner) : ConditionExpr;
public sealed record Arithmetic(ConditionExpr Left, ArithOp Op, ConditionExpr Right) : ConditionExpr;
public sealed record InList(ConditionExpr Item, IReadOnlyList<ConditionExpr> Items) : ConditionExpr;
public sealed record HourOf(ConditionExpr Timestamp) : ConditionExpr; // date-time helper: hour-of-day
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter Conditions.ConditionExprNodesTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core tests/Custodex.Core.Tests
git commit -m "feat: add concrete condition-body AST nodes"
```

---

### Task 2: The evaluation value model and result

**Files:**
- Create: `src/Custodex.Core/Conditions/CelValue.cs`
- Create: `src/Custodex.Core/Conditions/ConditionResult.cs`
- Test: `tests/Custodex.Core.Tests/Conditions/CelValueTests.cs`

**Interfaces:**
- Produces:
  - `CelValue` — a typed value wrapper with static factories `Bool`, `Int(long)`, `Double(double)`, `String`, `Timestamp(DateTimeOffset)`, a `CelKind Kind`, and typed accessors `AsBool/AsLong/AsDouble/AsString/AsTimestamp`.
  - `CelKind { Bool, Int, Double, String, Timestamp }`.
  - `ConditionResult(bool Allowed, string? Diagnostic)` with `ConditionResult.Allow`, `ConditionResult.Deny`, `ConditionResult.Error(string)`. An error is a deny carrying a diagnostic.
- Consumes: nothing beyond BCL.

`CelValue` is the internal representation; numeric coercion is centralized here (`Int` is widenable to `Double`). The evaluator never throws on a *data* problem — it produces `ConditionResult.Error`, which the Check/List layers treat as deny.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/Conditions/CelValueTests.cs
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class CelValueTests
{
    [Fact]
    public void Typed_factories_round_trip()
    {
        CelValue.Int(8).AsLong().ShouldBe(8);
        CelValue.Double(1.5).AsDouble().ShouldBe(1.5);
        CelValue.String("x").AsString().ShouldBe("x");
        CelValue.Bool(true).AsBool().ShouldBeTrue();
    }

    [Fact]
    public void Int_widens_to_double()
    {
        CelValue.Int(3).AsDouble().ShouldBe(3.0);
    }

    [Fact]
    public void Error_result_is_a_deny_with_a_diagnostic()
    {
        var r = ConditionResult.Error("missing attribute 'weight'");
        r.Allowed.ShouldBeFalse();
        r.Diagnostic.ShouldContain("weight");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter Conditions.CelValueTests`
Expected: FAIL — `CelValue`/`ConditionResult` do not exist.

- [ ] **Step 3: Implement the value model**

```csharp
// src/Custodex.Core/Conditions/CelValue.cs
namespace Custodex.Core.Conditions;

public enum CelKind { Bool, Int, Double, String, Timestamp }

public sealed class CelValue
{
    public CelKind Kind { get; }
    private readonly bool _bool;
    private readonly long _long;
    private readonly double _double;
    private readonly string _string;
    private readonly DateTimeOffset _timestamp;

    private CelValue(CelKind kind, bool b = false, long l = 0, double d = 0,
        string s = "", DateTimeOffset ts = default)
    {
        Kind = kind; _bool = b; _long = l; _double = d; _string = s; _timestamp = ts;
    }

    public static CelValue Bool(bool value) => new(CelKind.Bool, b: value);
    public static CelValue Int(long value) => new(CelKind.Int, l: value);
    public static CelValue Double(double value) => new(CelKind.Double, d: value);
    public static CelValue String(string value) => new(CelKind.String, s: value);
    public static CelValue Timestamp(DateTimeOffset value) => new(CelKind.Timestamp, ts: value);

    public bool AsBool() => _bool;
    public long AsLong() => _long;
    public double AsDouble() => Kind == CelKind.Int ? _long : _double;
    public string AsString() => _string;
    public DateTimeOffset AsTimestamp() => _timestamp;

    public bool IsNumeric => Kind is CelKind.Int or CelKind.Double;
}
```

```csharp
// src/Custodex.Core/Conditions/ConditionResult.cs
namespace Custodex.Core.Conditions;

public sealed record ConditionResult(bool Allowed, string? Diagnostic)
{
    public static readonly ConditionResult Allow = new(true, null);
    public static readonly ConditionResult Deny = new(false, null);
    public static ConditionResult Error(string diagnostic) => new(false, diagnostic);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter Conditions.CelValueTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core tests/Custodex.Core.Tests
git commit -m "feat: add CelValue model and ConditionResult"
```

---

### Task 3: The sandboxed `ConditionEvaluator`

**Files:**
- Create: `src/Custodex.Core/Conditions/ConditionEvaluator.cs`
- Test: `tests/Custodex.Core.Tests/Conditions/ConditionEvaluatorTests.cs`

**Interfaces:**
- Produces: `ConditionEvaluator.Evaluate(ConditionDef definition, IReadOnlyDictionary<string, object?> attributes, RequestContext context, IReadOnlyDictionary<string, object?> parameters) -> ConditionResult`.
- Consumes: `ConditionDef`, `RequestContext`, `SubjectRef` from `Custodex.Abstractions`; the body nodes and `CelValue`/`ConditionResult` from Tasks 1–2.

The evaluator walks the body to a `CelValue`, requiring the top-level result to be `Bool`. Reference resolution:

- `ParamRef(name)` reads `parameters[name]`, typed by the matching `ConditionParam.Type` (boxed value → `CelValue`).
- `AttributeRef(field)` reads `attributes[field]` (the synced resource attributes); a missing key is `ConditionResult.Error`.
- `ContextNow` is `CelValue.Timestamp(context.Now)`; `ContextSubject` is `CelValue.String(context.Subject.Id)`.
- `HourOf(ts)` requires a `Timestamp` operand and yields its `Hour` as `Int`.

Comparisons require comparable kinds (two numerics, two strings, two timestamps, two bools for `Eq`/`Ne`); arithmetic requires two numerics; `InList` compares the item against each list element by `Eq`. Any kind mismatch or missing reference returns `ConditionResult.Error` — never an exception. `is_creator` compares `context.subject` (the subject **id**) against the `created_by` attribute, which the consuming application syncs as the creator's bare user id (not `type:id`).

- [ ] **Step 1: Write the failing tests** (the three spec conditions)

```csharp
// tests/Custodex.Core.Tests/Conditions/ConditionEvaluatorTests.cs
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Conditions;

public class ConditionEvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoAttrs = new Dictionary<string, object?>();

    private static RequestContext Context(DateTimeOffset now, string subjectId) =>
        new(now, new SubjectRef("user", subjectId), new Dictionary<string, object?>());

    // within_hours(start, end) = context.now.hour >= start && context.now.hour < end
    private static ConditionDef WithinHours() => new(
        "within_hours",
        [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)],
        new BoolOp(
            new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef("start")),
            BoolConnective.And,
            new Compare(new HourOf(new ContextNow()), CompareOp.Lt, new ParamRef("end"))));

    // at_least(field, n) = resource[field] >= n  — 'field' selects the attribute name.
    private static ConditionDef AtLeastWeight() => new(
        "at_least",
        [new ConditionParam("n", ConditionType.Int)],
        new Compare(new AttributeRef("weight"), CompareOp.Ge, new ParamRef("n")));

    // is_creator() = resource.created_by == context.subject
    private static ConditionDef IsCreator() => new(
        "is_creator",
        [],
        new Compare(new AttributeRef("created_by"), CompareOp.Eq, new ContextSubject()));

    [Fact]
    public void Within_hours_allows_inside_window_and_denies_outside()
    {
        var def = WithinHours();
        var p = new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 };

        ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero), "dr-smith"), p)
            .Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def, NoAttrs,
            Context(new DateTimeOffset(2026, 6, 23, 20, 0, 0, TimeSpan.Zero), "dr-smith"), p)
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public void At_least_compares_attribute_to_parameter()
    {
        var def = AtLeastWeight();
        var ctx = Context(DateTimeOffset.UnixEpoch, "dr-smith");

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = 50 }, ctx,
            new Dictionary<string, object?> { ["n"] = 30 }).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = 10 }, ctx,
            new Dictionary<string, object?> { ["n"] = 30 }).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Is_creator_compares_created_by_to_subject_id()
    {
        var def = IsCreator();
        var noParams = new Dictionary<string, object?>();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["created_by"] = "dr-smith" },
            Context(DateTimeOffset.UnixEpoch, "dr-smith"), noParams).Allowed.ShouldBeTrue();

        ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["created_by"] = "alice" },
            Context(DateTimeOffset.UnixEpoch, "dr-smith"), noParams).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Missing_attribute_is_a_deny_with_a_diagnostic_not_an_exception()
    {
        var def = AtLeastWeight();
        var result = ConditionEvaluator.Evaluate(def, NoAttrs,   // no 'weight'
            Context(DateTimeOffset.UnixEpoch, "dr-smith"),
            new Dictionary<string, object?> { ["n"] = 30 });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldContain("weight");
    }

    [Fact]
    public void Type_mismatch_is_a_deny_with_a_diagnostic()
    {
        var def = AtLeastWeight();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = "heavy" },   // string vs int compare
            Context(DateTimeOffset.UnixEpoch, "dr-smith"),
            new Dictionary<string, object?> { ["n"] = 30 });

        result.Allowed.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter Conditions.ConditionEvaluatorTests`
Expected: FAIL — `ConditionEvaluator` does not exist.

- [ ] **Step 3: Implement the evaluator**

```csharp
// src/Custodex.Core/Conditions/ConditionEvaluator.cs
using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

public static class ConditionEvaluator
{
    private sealed class EvalException(string message) : Exception(message);

    public static ConditionResult Evaluate(
        ConditionDef definition,
        IReadOnlyDictionary<string, object?> attributes,
        RequestContext context,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var paramTypes = definition.Parameters
            .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
        try
        {
            var value = Eval(definition.Body, attributes, context, parameters, paramTypes);
            if (value.Kind != CelKind.Bool)
                return ConditionResult.Error(
                    $"Condition '{definition.Name}' body did not evaluate to a boolean.");
            return value.AsBool() ? ConditionResult.Allow : ConditionResult.Deny;
        }
        catch (EvalException ex)
        {
            return ConditionResult.Error($"Condition '{definition.Name}': {ex.Message}");
        }
    }

    private static CelValue Eval(
        ConditionExpr expr,
        IReadOnlyDictionary<string, object?> attributes,
        RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes) => expr switch
    {
        LiteralBool l => CelValue.Bool(l.Value),
        LiteralInt l => CelValue.Int(l.Value),
        LiteralDouble l => CelValue.Double(l.Value),
        LiteralString l => CelValue.String(l.Value),

        ParamRef p => ResolveParam(p.Name, parameters, paramTypes),
        AttributeRef a => ResolveAttribute(a.Field, attributes),
        ContextNow => CelValue.Timestamp(context.Now),
        ContextSubject => CelValue.String(context.Subject.Id),

        HourOf h => EvalHour(h, attributes, context, parameters, paramTypes),
        Not n => CelValue.Bool(!ExpectBool(Eval(n.Inner, attributes, context, parameters, paramTypes))),
        BoolOp b => EvalBool(b, attributes, context, parameters, paramTypes),
        Compare c => EvalCompare(c, attributes, context, parameters, paramTypes),
        Arithmetic ar => EvalArith(ar, attributes, context, parameters, paramTypes),
        InList il => EvalInList(il, attributes, context, parameters, paramTypes),

        _ => throw new EvalException("unsupported expression node."),
    };

    private static CelValue ResolveParam(
        string name, IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        if (!paramTypes.TryGetValue(name, out var type))
            throw new EvalException($"parameter '{name}' is not declared.");
        if (!parameters.TryGetValue(name, out var raw) || raw is null)
            throw new EvalException($"parameter '{name}' is missing.");
        return type switch
        {
            ConditionType.Bool when raw is bool b => CelValue.Bool(b),
            ConditionType.Int when raw is int or long => CelValue.Int(Convert.ToInt64(raw)),
            ConditionType.Long when raw is int or long => CelValue.Int(Convert.ToInt64(raw)),
            ConditionType.Double when raw is int or long or double => CelValue.Double(Convert.ToDouble(raw)),
            ConditionType.String when raw is string s => CelValue.String(s),
            ConditionType.Timestamp when raw is DateTimeOffset dto => CelValue.Timestamp(dto),
            ConditionType.Timestamp when raw is DateTime dt => CelValue.Timestamp(dt),
            _ => throw new EvalException($"parameter '{name}' value does not match declared type {type}."),
        };
    }

    private static CelValue ResolveAttribute(string field, IReadOnlyDictionary<string, object?> attributes)
    {
        if (!attributes.TryGetValue(field, out var raw) || raw is null)
            throw new EvalException($"attribute '{field}' is missing.");
        return raw switch
        {
            bool b => CelValue.Bool(b),
            int or long => CelValue.Int(Convert.ToInt64(raw)),
            double or float => CelValue.Double(Convert.ToDouble(raw)),
            string s => CelValue.String(s),
            DateTimeOffset dto => CelValue.Timestamp(dto),
            DateTime dt => CelValue.Timestamp(dt),
            _ => throw new EvalException($"attribute '{field}' has an unsupported type."),
        };
    }

    private static CelValue EvalHour(
        HourOf h, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var ts = Eval(h.Timestamp, attributes, context, parameters, paramTypes);
        if (ts.Kind != CelKind.Timestamp)
            throw new EvalException("hour() requires a timestamp operand.");
        return CelValue.Int(ts.AsTimestamp().Hour);
    }

    private static CelValue EvalBool(
        BoolOp b, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var left = ExpectBool(Eval(b.Left, attributes, context, parameters, paramTypes));
        // Short-circuit.
        if (b.Op == BoolConnective.And && !left) return CelValue.Bool(false);
        if (b.Op == BoolConnective.Or && left) return CelValue.Bool(true);
        var right = ExpectBool(Eval(b.Right, attributes, context, parameters, paramTypes));
        return CelValue.Bool(right);
    }

    private static CelValue EvalCompare(
        Compare c, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var l = Eval(c.Left, attributes, context, parameters, paramTypes);
        var r = Eval(c.Right, attributes, context, parameters, paramTypes);
        return CelValue.Bool(CompareValues(l, r, c.Op));
    }

    private static bool CompareValues(CelValue l, CelValue r, CompareOp op)
    {
        if (l.IsNumeric && r.IsNumeric)
            return ApplyOrder(l.AsDouble().CompareTo(r.AsDouble()), op);
        if (l.Kind == CelKind.String && r.Kind == CelKind.String)
            return ApplyOrder(string.CompareOrdinal(l.AsString(), r.AsString()), op);
        if (l.Kind == CelKind.Timestamp && r.Kind == CelKind.Timestamp)
            return ApplyOrder(l.AsTimestamp().CompareTo(r.AsTimestamp()), op);
        if (l.Kind == CelKind.Bool && r.Kind == CelKind.Bool && op is CompareOp.Eq or CompareOp.Ne)
            return op == CompareOp.Eq ? l.AsBool() == r.AsBool() : l.AsBool() != r.AsBool();
        throw new EvalException($"cannot compare {l.Kind} with {r.Kind}.");
    }

    private static bool ApplyOrder(int cmp, CompareOp op) => op switch
    {
        CompareOp.Eq => cmp == 0,
        CompareOp.Ne => cmp != 0,
        CompareOp.Lt => cmp < 0,
        CompareOp.Le => cmp <= 0,
        CompareOp.Gt => cmp > 0,
        CompareOp.Ge => cmp >= 0,
        _ => throw new EvalException("unsupported comparison operator."),
    };

    private static CelValue EvalArith(
        Arithmetic ar, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var l = Eval(ar.Left, attributes, context, parameters, paramTypes);
        var r = Eval(ar.Right, attributes, context, parameters, paramTypes);
        if (!l.IsNumeric || !r.IsNumeric)
            throw new EvalException("arithmetic requires numeric operands.");
        var useInt = l.Kind == CelKind.Int && r.Kind == CelKind.Int;
        double dl = l.AsDouble(), dr = r.AsDouble();
        double result = ar.Op switch
        {
            ArithOp.Add => dl + dr,
            ArithOp.Sub => dl - dr,
            ArithOp.Mul => dl * dr,
            ArithOp.Div => dr == 0 ? throw new EvalException("division by zero.") : dl / dr,
            _ => throw new EvalException("unsupported arithmetic operator."),
        };
        return useInt && ar.Op != ArithOp.Div ? CelValue.Int((long)result) : CelValue.Double(result);
    }

    private static CelValue EvalInList(
        InList il, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var item = Eval(il.Item, attributes, context, parameters, paramTypes);
        foreach (var element in il.Items)
        {
            var e = Eval(element, attributes, context, parameters, paramTypes);
            if (CompareValues(item, e, CompareOp.Eq))
                return CelValue.Bool(true);
        }
        return CelValue.Bool(false);
    }

    private static bool ExpectBool(CelValue value) =>
        value.Kind == CelKind.Bool ? value.AsBool()
            : throw new EvalException("expected a boolean operand.");
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter Conditions.ConditionEvaluatorTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Core tests/Custodex.Core.Tests
git commit -m "feat: add sandboxed typed condition evaluator"
```

---

### Task 4: `SchemaBuilder.Condition(name, params, bodyExpr)` overload

**Files:**
- Modify: `src/Custodex.Core/SchemaBuilder.cs`
- Create: `src/Custodex.Core/ConditionBodyBuilder.cs`
- Test: `tests/Custodex.Core.Tests/ConditionBodyBuilderTests.cs`

**Interfaces:**
- Produces:
  - `ConditionBodyBuilder` — a small CEL-shaped fluent helper exposing `Param(string)`, `Attribute(string)`, `Now()`, `Subject()`, `Const(...)` factories and the operator combinators `Hour`, `Ge`/`Gt`/`Le`/`Lt`/`Eq`/`Ne`, `And`/`Or`/`Not`, `Add`/`Sub`/`Mul`/`Div`, `In`, returning `ConditionExpr`.
  - `SchemaBuilder.Condition(string name, Action<ConditionParamBuilder> params, Func<ConditionBodyBuilder, ConditionExpr> body)` — declares a condition with a real body, replacing `EmptyConditionBody`.
- Consumes: `ConditionParamBuilder` (from `m0/02`), the body nodes (Task 1), `ConditionDef`, `ConditionExpr`.

The params-only overload from `m0/02` (`Condition(string, Action<ConditionParamBuilder>)`) must keep working — its test (`SchemaBuilderTests`) still passes. The new overload is added alongside it.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Core.Tests/ConditionBodyBuilderTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests;

public class ConditionBodyBuilderTests
{
    [Fact]
    public void Condition_overload_attaches_a_real_body()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("within_hours",
                p => p.Int("start").Int("end"),
                b => b.And(
                    b.Ge(b.Hour(b.Now()), b.Param("start")),
                    b.Lt(b.Hour(b.Now()), b.Param("end"))))
            .Build();

        var cond = schema.Conditions.Single();
        cond.Name.ShouldBe("within_hours");
        cond.Parameters.Count.ShouldBe(2);
        cond.Body.ShouldBeOfType<BoolOp>().Op.ShouldBe(BoolConnective.And);
    }

    [Fact]
    public void Params_only_overload_still_attaches_empty_body()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("legacy", c => c.Int("n"))
            .Build();

        schema.Conditions.Single().Body.ShouldBeOfType<EmptyConditionBody>();
    }

    [Fact]
    public void Evaluator_runs_a_builder_produced_body()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("at_least",
                p => p.Int("n"),
                b => b.Ge(b.Attribute("weight"), b.Param("n")))
            .Build();

        var def = schema.Conditions.Single();
        var result = ConditionEvaluator.Evaluate(def,
            new Dictionary<string, object?> { ["weight"] = 50 },
            new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "dr-smith"),
                new Dictionary<string, object?>()),
            new Dictionary<string, object?> { ["n"] = 30 });

        result.Allowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter ConditionBodyBuilderTests`
Expected: FAIL — `ConditionBodyBuilder` and the new overload do not exist.

- [ ] **Step 3: Implement the body builder**

```csharp
// src/Custodex.Core/ConditionBodyBuilder.cs
using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core;

public sealed class ConditionBodyBuilder
{
    public ConditionExpr Param(string name) => new ParamRef(name);
    public ConditionExpr Attribute(string field) => new AttributeRef(field);
    public ConditionExpr Now() => new ContextNow();
    public ConditionExpr Subject() => new ContextSubject();

    public ConditionExpr Const(bool value) => new LiteralBool(value);
    public ConditionExpr Const(long value) => new LiteralInt(value);
    public ConditionExpr Const(double value) => new LiteralDouble(value);
    public ConditionExpr Const(string value) => new LiteralString(value);

    public ConditionExpr Hour(ConditionExpr timestamp) => new HourOf(timestamp);

    public ConditionExpr Eq(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Eq, r);
    public ConditionExpr Ne(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Ne, r);
    public ConditionExpr Lt(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Lt, r);
    public ConditionExpr Le(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Le, r);
    public ConditionExpr Gt(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Gt, r);
    public ConditionExpr Ge(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Ge, r);

    public ConditionExpr And(ConditionExpr l, ConditionExpr r) => new BoolOp(l, BoolConnective.And, r);
    public ConditionExpr Or(ConditionExpr l, ConditionExpr r) => new BoolOp(l, BoolConnective.Or, r);
    public ConditionExpr Not(ConditionExpr inner) => new Not(inner);

    public ConditionExpr Add(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Add, r);
    public ConditionExpr Sub(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Sub, r);
    public ConditionExpr Mul(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Mul, r);
    public ConditionExpr Div(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Div, r);

    public ConditionExpr In(ConditionExpr item, params ConditionExpr[] items) => new InList(item, items);
}
```

- [ ] **Step 4: Add the `Condition` overload to `SchemaBuilder`**

```csharp
// src/Custodex.Core/SchemaBuilder.cs  (add this method to the SchemaBuilder class)
    public SchemaBuilder Condition(
        string name, Action<ConditionParamBuilder> @params, Func<ConditionBodyBuilder, ConditionExpr> body)
    {
        var paramBuilder = new ConditionParamBuilder();
        @params(paramBuilder);
        var bodyExpr = body(new ConditionBodyBuilder());
        _conditions.Add(new ConditionDef(name, paramBuilder.Build(), bodyExpr));
        return this;
    }
```

> The params-only overload and `EmptyConditionBody` record from `m0/02` are unchanged and remain in `SchemaBuilder.cs`.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter ConditionBodyBuilderTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the full Core suite to confirm the m0/02 builder test still passes**

Run: `dotnet test tests/Custodex.Core.Tests`
Expected: PASS — including `SchemaBuilderTests` (the params-only `Condition` overload).

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Core tests/Custodex.Core.Tests
git commit -m "feat: add Condition(name, params, body) builder overload with CEL-shaped body builder"
```

---

### Task 5: Extend `SchemaValidator` to type-check condition bodies

**Files:**
- Create: `src/Custodex.Core/Validation/ConditionBodyChecker.cs`
- Modify: `src/Custodex.Core/Validation/SchemaValidator.cs`
- Test: `tests/Custodex.Core.Tests/Validation/ConditionBodyCheckTests.cs`

**Interfaces:**
- Produces: `ConditionBodyChecker.Check(ConditionDef) -> IReadOnlyList<string>` (per-condition body errors), called from `SchemaValidator.Validate` over every `ConditionDef` whose body is not `EmptyConditionBody`.
- Consumes: the body nodes (Task 1), `ConditionDef`, `ConditionParam`, `ConditionType`.

This is the body-AST type-check deferred from `m0/03` (which only checks parameter *values*). It statically infers each node's `CelKind` and reports: a `ParamRef` to an undeclared parameter; a `Compare`/`Arithmetic`/`BoolOp`/`HourOf` over incompatible inferred kinds; and a body whose top-level kind is not `Bool`. An `AttributeRef` is treated as kind-unknown (its type is only known at request time), so comparisons involving an attribute are not statically rejected; the runtime evaluator owns that. `EmptyConditionBody` is skipped (the params-only overload declares no body to check).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Core.Tests/Validation/ConditionBodyCheckTests.cs
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Validation;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Validation;

public class ConditionBodyCheckTests
{
    [Fact]
    public void Well_typed_within_hours_body_passes()
    {
        var schema = new SchemaBuilder("v1")
            .Condition("within_hours",
                p => p.Int("start").Int("end"),
                b => b.And(
                    b.Ge(b.Hour(b.Now()), b.Param("start")),
                    b.Lt(b.Hour(b.Now()), b.Param("end"))))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Body_referencing_an_undeclared_parameter_fails()
    {
        var def = new ConditionDef("bad", [new ConditionParam("n", ConditionType.Int)],
            new Compare(new ParamRef("ghost"), CompareOp.Ge, new LiteralInt(1)));
        var schema = new Schema("v1", [], [def]);

        var result = SchemaValidator.Validate(schema);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("ghost"));
    }

    [Fact]
    public void Body_comparing_a_string_param_to_a_number_fails()
    {
        var def = new ConditionDef("bad", [new ConditionParam("s", ConditionType.String)],
            new Compare(new ParamRef("s"), CompareOp.Lt, new LiteralInt(1)));
        var schema = new Schema("v1", [], [def]);

        SchemaValidator.Validate(schema).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Non_boolean_top_level_body_fails()
    {
        var def = new ConditionDef("bad", [new ConditionParam("n", ConditionType.Int)],
            new ParamRef("n"));   // top-level Int, not Bool
        var schema = new Schema("v1", [], [def]);

        SchemaValidator.Validate(schema).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Empty_body_from_params_only_overload_is_skipped()
    {
        var schema = new SchemaBuilder("v1").Condition("legacy", c => c.Int("n")).Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Core.Tests --filter Validation.ConditionBodyCheckTests`
Expected: FAIL — `ConditionBodyChecker` does not exist and `SchemaValidator` does not call it.

- [ ] **Step 3: Implement the body checker**

```csharp
// src/Custodex.Core/Validation/ConditionBodyChecker.cs
using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core.Validation;

public static class ConditionBodyChecker
{
    // Inferred static kind; null means "unknown until request time" (attributes).
    private enum K { Bool, Number, String, Timestamp, Unknown }

    public static IReadOnlyList<string> Check(ConditionDef definition)
    {
        var errors = new List<string>();
        var paramKinds = definition.Parameters
            .ToDictionary(p => p.Name, p => KindOf(p.Type), StringComparer.Ordinal);

        var top = Infer(definition.Body, definition.Name, paramKinds, errors);
        if (top is not (K.Bool or K.Unknown))
            errors.Add($"Condition '{definition.Name}' body must evaluate to a boolean.");

        return errors;
    }

    private static K KindOf(ConditionType type) => type switch
    {
        ConditionType.Bool => K.Bool,
        ConditionType.Int or ConditionType.Long or ConditionType.Double => K.Number,
        ConditionType.String => K.String,
        ConditionType.Timestamp => K.Timestamp,
        _ => K.Unknown,
    };

    private static K Infer(
        ConditionExpr expr, string condition,
        IReadOnlyDictionary<string, K> paramKinds, List<string> errors)
    {
        switch (expr)
        {
            case LiteralBool: return K.Bool;
            case LiteralInt or LiteralDouble: return K.Number;
            case LiteralString: return K.String;
            case ContextNow: return K.Timestamp;
            case ContextSubject: return K.String;
            case AttributeRef: return K.Unknown;

            case ParamRef p:
                if (!paramKinds.TryGetValue(p.Name, out var k))
                {
                    errors.Add($"Condition '{condition}' body references undeclared parameter '{p.Name}'.");
                    return K.Unknown;
                }
                return k;

            case HourOf h:
                var inner = Infer(h.Timestamp, condition, paramKinds, errors);
                if (inner is not (K.Timestamp or K.Unknown))
                    errors.Add($"Condition '{condition}' hour() requires a timestamp.");
                return K.Number;

            case Not n:
                var ni = Infer(n.Inner, condition, paramKinds, errors);
                if (ni is not (K.Bool or K.Unknown))
                    errors.Add($"Condition '{condition}' not() requires a boolean.");
                return K.Bool;

            case BoolOp b:
                Expect(Infer(b.Left, condition, paramKinds, errors), K.Bool, condition, "&&/||", errors);
                Expect(Infer(b.Right, condition, paramKinds, errors), K.Bool, condition, "&&/||", errors);
                return K.Bool;

            case Compare c:
                CheckComparable(
                    Infer(c.Left, condition, paramKinds, errors),
                    Infer(c.Right, condition, paramKinds, errors), condition, errors);
                return K.Bool;

            case Arithmetic a:
                Expect(Infer(a.Left, condition, paramKinds, errors), K.Number, condition, "arithmetic", errors);
                Expect(Infer(a.Right, condition, paramKinds, errors), K.Number, condition, "arithmetic", errors);
                return K.Number;

            case InList il:
                var itemKind = Infer(il.Item, condition, paramKinds, errors);
                foreach (var element in il.Items)
                    CheckComparable(itemKind, Infer(element, condition, paramKinds, errors), condition, errors);
                return K.Bool;

            default:
                errors.Add($"Condition '{condition}' body contains an unsupported node.");
                return K.Unknown;
        }
    }

    private static void Expect(K actual, K expected, string condition, string op, List<string> errors)
    {
        if (actual is not (K.Unknown) && actual != expected)
            errors.Add($"Condition '{condition}' {op} requires {expected} but found {actual}.");
    }

    private static void CheckComparable(K left, K right, string condition, List<string> errors)
    {
        if (left == K.Unknown || right == K.Unknown) return;
        if (left != right)
            errors.Add($"Condition '{condition}' compares incompatible kinds {left} and {right}.");
    }
}
```

- [ ] **Step 4: Wire the body checker into `SchemaValidator`**

```csharp
// src/Custodex.Core/Validation/SchemaValidator.cs  (in Validate, after IndexConditions and before the per-permission loop)
        foreach (var cond in schema.Conditions)
            if (cond.Body is not EmptyConditionBody)
                errors.AddRange(ConditionBodyChecker.Check(cond));
```

> `EmptyConditionBody` is the placeholder record declared in `Custodex.Core` (`m0/02`'s `SchemaBuilder.cs`); it is in scope here without an extra using.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Core.Tests --filter Validation.ConditionBodyCheckTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test tests/Custodex.Core.Tests`
Expected: PASS — `m0/03` validation tests and `m0/02` builder tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/Custodex.Core tests/Custodex.Core.Tests
git commit -m "feat: type-check condition bodies during schema validation"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] The body AST is CEL-shaped (literals, param/attribute/context refs, comparison/boolean/arithmetic/`in`/hour ops) and derives from `Custodex.Abstractions.ConditionExpr`.
- [ ] `ConditionEvaluator` evaluates `within_hours`, `at_least`, and `is_creator`; a missing attribute or type mismatch is a deny with a diagnostic, not an exception.
- [ ] Ambient time enters only through `RequestContext.Now`; no `DateTime.Now`/`DateTimeOffset.UtcNow` in the evaluator.
- [ ] `SchemaBuilder.Condition(name, params, bodyExpr)` attaches a real body; the params-only overload from `m0/02` still produces `EmptyConditionBody` and its test still passes.
- [ ] `SchemaValidator` type-checks non-empty condition bodies and skips `EmptyConditionBody`.
