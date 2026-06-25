using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

/// <summary>
/// The substitutable seam that decides whether a schema-declared condition holds. The
/// authorizers depend on this interface, so a consumer can supply an alternative predicate
/// engine. Implementations evaluate against the bound invocation parameters, the object's
/// synced attributes, and the request context, and treat any failure or missing attribute
/// as a denial rather than throwing.
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
    /// <returns><see langword="true"/> if the condition is satisfied; otherwise <see langword="false"/>.</returns>
    bool Evaluate(
        ConditionDef definition,
        ConditionRef invocation,
        IReadOnlyDictionary<string, object?> resourceAttributes,
        RequestContext context);
}
