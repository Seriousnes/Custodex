using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// Evaluates a schema-declared condition against synced resource attributes,
/// request context, and the tuple's stored parameters. The real implementation
/// (<c>ConditionEvaluator</c>) is built in m0/06; this signature is its contract.
/// </summary>
public interface IConditionEvaluator
{
    bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context);
}
