using Relkit.Abstractions;

namespace Relkit.Core.Conditions;

/// <summary>Treats every condition as satisfied. Used before m0/06 and in pure-ReBAC tests.</summary>
public sealed class NullConditionEvaluator : IConditionEvaluator
{
    public bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context) => true;
}
