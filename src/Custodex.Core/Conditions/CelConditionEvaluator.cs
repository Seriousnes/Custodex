using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// The default <see cref="IConditionEvaluator"/>, adapting the static <see cref="ConditionEvaluator"/>
/// to the seam the authorizers consume. It forwards the invocation's bound parameters and the object's
/// synced attributes, and collapses the <see cref="ConditionResult"/> to a bool, so any denial or
/// evaluation error becomes <see langword="false"/> (default-deny).
/// </summary>
public sealed class CelConditionEvaluator : IConditionEvaluator
{
    /// <inheritdoc/>
    public bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context)
        => ConditionEvaluator.Evaluate(definition, resourceAttributes, context, invocation.Parameters).Allowed;
}
