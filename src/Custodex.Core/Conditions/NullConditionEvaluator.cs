using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// An <see cref="IConditionEvaluator"/> that treats every condition as satisfied. Use it where
/// conditions are ignored, such as pure relationship-only authorization.
/// </summary>
public sealed class NullConditionEvaluator : IConditionEvaluator
{
    /// <inheritdoc/>
    public ConditionResult Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context) => ConditionResult.Allow;
}
