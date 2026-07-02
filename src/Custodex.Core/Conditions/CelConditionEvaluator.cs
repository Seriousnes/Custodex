using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// The default <see cref="IConditionEvaluator"/>, adapting the static <see cref="ConditionEvaluator"/>
/// to the seam the authorizers consume. It forwards the invocation's bound parameters and the object's
/// synced attributes and returns the full <see cref="ConditionResult"/>, so a missing attribute surfaces
/// as missing-context rather than collapsing to a denial.
/// </summary>
public sealed class CelConditionEvaluator : IConditionEvaluator
{
    /// <inheritdoc/>
    public ConditionResult Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context)
        => ConditionEvaluator.Evaluate(definition, resourceAttributes, context, invocation.Parameters);
}
