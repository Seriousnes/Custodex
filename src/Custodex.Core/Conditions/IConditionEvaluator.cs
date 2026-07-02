using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// The substitutable seam that decides how a schema-declared condition resolves. The
/// authorizers depend on this interface, so a consumer can supply an alternative predicate
/// engine. Implementations evaluate against the bound invocation parameters, the object's
/// synced attributes, and the request context, and report a failure or absent attribute as a
/// <see cref="ConditionResult"/> rather than throwing.
/// </summary>
public interface IConditionEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="definition"/> as invoked by <paramref name="invocation"/>.
    /// </summary>
    /// <param name="definition">The condition's declared parameters and body.</param>
    /// <param name="invocation">The reference that binds values to the condition's parameters.</param>
    /// <param name="resourceAttributes">The object's attribute bag the body may read.</param>
    /// <param name="context">The request context, supplying ambient values such as the time and subject.</param>
    /// <returns>How the condition resolved: satisfied, unsatisfied, or missing-context with the absent keys.</returns>
    ConditionResult Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context);
}
