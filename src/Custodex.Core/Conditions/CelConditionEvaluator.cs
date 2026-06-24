using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// Adapts the static <see cref="ConditionEvaluator"/> to the injectable
/// <see cref="IConditionEvaluator"/> seam that EngineDrivenAuthorizer consumes:
/// forwards the tuple/invocation parameters and synced resource attributes, and
/// maps the <see cref="ConditionResult"/> to a bool (Allow → true; Deny/Error →
/// false, honouring default-deny).
/// </summary>
public sealed class CelConditionEvaluator : IConditionEvaluator
{
    public bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context)
        => ConditionEvaluator.Evaluate(definition, resourceAttributes, context, invocation.Parameters).Allowed;
}
