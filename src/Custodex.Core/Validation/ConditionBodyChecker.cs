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
