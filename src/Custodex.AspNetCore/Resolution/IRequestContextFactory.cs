using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>Builds the <see cref="RequestContext"/> (time and attributes) for a decision.</summary>
public interface IRequestContextFactory
{
    /// <summary>Creates a request context for <paramref name="subject"/>.</summary>
    /// <param name="subject">The resolved subject.</param>
    /// <param name="context">The resolution inputs.</param>
    /// <returns>The request context to evaluate with.</returns>
    RequestContext Create(SubjectRef subject, CustodexResolutionContext context);
}
