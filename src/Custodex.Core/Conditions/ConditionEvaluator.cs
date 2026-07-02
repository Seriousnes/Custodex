using System.Globalization;

using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// Evaluates a condition's expression tree over its bound parameters, the object's attribute bag,
/// and the request context, in three-valued (Kleene) logic. The body must reduce to a boolean; a type
/// mismatch, a missing parameter, division by zero, or a non-boolean body yields a denial carrying a
/// diagnostic. An attribute the body reads that is absent from both the object's attributes and the
/// request context yields a missing-context result naming that key, since supplying it could change the outcome.
/// </summary>
public static class ConditionEvaluator
{
    private sealed class EvalException(string message) : Exception(message);

    /// <summary>
    /// Evaluates <paramref name="definition"/>'s body and reports how it resolved.
    /// </summary>
    /// <param name="definition">The condition's declared parameters and body.</param>
    /// <param name="attributes">The object's attribute bag the body may read; the request context's attributes are consulted as a fallback.</param>
    /// <param name="context">The request context, supplying ambient values such as the time and subject and caller-provided attributes.</param>
    /// <param name="parameters">The values bound to the condition's declared parameters.</param>
    /// <returns>Satisfied when the body evaluated to <see langword="true"/>; unsatisfied on a definite failure; missing-context when a read attribute was absent.</returns>
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
            if (value.IsUnknown)
                return ConditionResult.Missing(value.MissingKeys,
                    $"Condition '{definition.Name}': attribute(s) [{string.Join(", ", value.MissingKeys)}] were not available.");
            if (value.Kind != CelKind.Bool)
                return ConditionResult.Error(
                    $"Condition '{definition.Name}' body did not evaluate to a boolean.");
            return value.AsBool() ? ConditionResult.Allow : ConditionResult.Deny;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        AttributeRef a => ResolveAttribute(a.Field, a.Type, attributes, context),
        ContextNow => CelValue.Timestamp(context.Now),
        ContextSubject => CelValue.String(context.Subject.Id),

        HourOf h => EvalHour(h, attributes, context, parameters, paramTypes),
        Not n => EvalNot(n, attributes, context, parameters, paramTypes),
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
            ConditionType.Timestamp when raw is string ts => CelValue.Timestamp(ParseTimestamp(ts)),
            _ => throw new EvalException($"parameter '{name}' value does not match declared type {type}."),
        };
    }

    private static CelValue ResolveAttribute(
        string field, ConditionType? declaredType,
        IReadOnlyDictionary<string, object?> attributes, RequestContext context)
    {
        if (!TryResolveAttributeValue(field, attributes, context, out var raw))
            return CelValue.Unknown([field]);
        if (declaredType == ConditionType.Timestamp)
            return raw switch
            {
                DateTimeOffset dto => CelValue.Timestamp(dto),
                DateTime dt => CelValue.Timestamp(dt),
                string ts => CelValue.Timestamp(ParseTimestamp(ts)),
                _ => throw new EvalException(
                    $"attribute '{field}' value does not match declared type {declaredType}."),
            };
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

    private static bool TryResolveAttributeValue(
        string field, IReadOnlyDictionary<string, object?> attributes, RequestContext context, out object raw)
    {
        if (attributes.TryGetValue(field, out var fromResource) && fromResource is not null)
        {
            raw = fromResource;
            return true;
        }
        if (context.Attributes.TryGetValue(field, out var fromContext) && fromContext is not null)
        {
            raw = fromContext;
            return true;
        }
        raw = null!;
        return false;
    }

    private static CelValue EvalHour(
        HourOf h, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var ts = CoerceToTimestamp(Eval(h.Timestamp, attributes, context, parameters, paramTypes));
        if (ts.IsUnknown) return ts;
        if (ts.Kind != CelKind.Timestamp)
            throw new EvalException("hour() requires a timestamp operand.");
        return CelValue.Int(ts.AsTimestamp().Hour);
    }

    private static CelValue EvalNot(
        Not n, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var inner = Eval(n.Inner, attributes, context, parameters, paramTypes);
        return inner.IsUnknown ? inner : CelValue.Bool(!ExpectBool(inner));
    }

    private static CelValue EvalBool(
        BoolOp b, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var left = Eval(b.Left, attributes, context, parameters, paramTypes);
        if (IsAbsorbing(left, b.Op, out var leftDecides)) return CelValue.Bool(leftDecides);

        var right = Eval(b.Right, attributes, context, parameters, paramTypes);
        if (IsAbsorbing(right, b.Op, out var rightDecides)) return CelValue.Bool(rightDecides);

        if (left.IsUnknown || right.IsUnknown)
            return CelValue.Unknown(Merge(left.MissingKeys, right.MissingKeys));

        var l = ExpectBool(left);
        var r = ExpectBool(right);
        return CelValue.Bool(b.Op == BoolConnective.And ? l && r : l || r);
    }

    private static bool IsAbsorbing(CelValue value, BoolConnective op, out bool decidedValue)
    {
        decidedValue = op == BoolConnective.Or;
        if (value.Kind != CelKind.Bool) return false;
        return op == BoolConnective.And ? !value.AsBool() : value.AsBool();
    }

    private static CelValue EvalCompare(
        Compare c, IReadOnlyDictionary<string, object?> attributes, RequestContext context,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, ConditionType> paramTypes)
    {
        var l = Eval(c.Left, attributes, context, parameters, paramTypes);
        var r = Eval(c.Right, attributes, context, parameters, paramTypes);
        if (l.IsUnknown || r.IsUnknown)
            return CelValue.Unknown(Merge(l.MissingKeys, r.MissingKeys));
        return CelValue.Bool(CompareValues(l, r, c.Op));
    }

    private static bool CompareValues(CelValue l, CelValue r, CompareOp op)
    {
        (l, r) = CoerceTimestampOperands(l, r);
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

    private static (CelValue Left, CelValue Right) CoerceTimestampOperands(CelValue l, CelValue r)
    {
        if (l.Kind == CelKind.Timestamp && r.Kind == CelKind.String)
            return (l, CoerceToTimestamp(r));
        if (l.Kind == CelKind.String && r.Kind == CelKind.Timestamp)
            return (CoerceToTimestamp(l), r);
        return (l, r);
    }

    private static CelValue CoerceToTimestamp(CelValue value) =>
        value.Kind == CelKind.String ? CelValue.Timestamp(ParseTimestamp(value.AsString())) : value;

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : throw new EvalException($"value '{text}' is not a valid timestamp.");

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
        if (l.IsUnknown || r.IsUnknown)
            return CelValue.Unknown(Merge(l.MissingKeys, r.MissingKeys));
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
        if (item.IsUnknown) return item;
        IReadOnlyList<string> unknownKeys = [];
        foreach (var element in il.Items)
        {
            var e = Eval(element, attributes, context, parameters, paramTypes);
            if (e.IsUnknown) { unknownKeys = Merge(unknownKeys, e.MissingKeys); continue; }
            if (CompareValues(item, e, CompareOp.Eq))
                return CelValue.Bool(true);
        }
        return unknownKeys.Count > 0 ? CelValue.Unknown(unknownKeys) : CelValue.Bool(false);
    }

    private static bool ExpectBool(CelValue value) =>
        value.Kind == CelKind.Bool ? value.AsBool()
            : throw new EvalException("expected a boolean operand.");

    private static IReadOnlyList<string> Merge(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var set = new SortedSet<string>(a, StringComparer.Ordinal);
        foreach (var key in b) set.Add(key);
        return [.. set];
    }
}
