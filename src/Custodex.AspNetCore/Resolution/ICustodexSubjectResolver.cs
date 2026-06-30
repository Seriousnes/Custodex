using System.Security.Claims;

using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>Maps the authenticated principal to the Custodex subject a decision is evaluated for.</summary>
public interface ICustodexSubjectResolver
{
    /// <summary>Attempts to resolve a subject from <paramref name="user"/>.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="subject">The resolved subject when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a subject was resolved; otherwise <see langword="false"/>, which denies the request.</returns>
    bool TryResolve(ClaimsPrincipal user, out SubjectRef subject);
}
