using Custodex.Abstractions;

namespace Custodex.Core.Validation;

/// <summary>
/// Checks a set of supplied parameter values against a condition's declared parameters: every
/// declared parameter must be present with a value of the expected type, and no undeclared
/// parameter may be supplied.
/// </summary>
public static class ConditionParamChecker
{
    /// <summary>Checks supplied parameter values against a condition's declarations.</summary>
    /// <param name="definition">The condition whose declared parameters define what is expected.</param>
    /// <param name="parameters">The supplied parameter values, keyed by parameter name.</param>
    /// <returns>One message per missing, mistyped, or undeclared parameter, or an empty list when all match.</returns>
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
