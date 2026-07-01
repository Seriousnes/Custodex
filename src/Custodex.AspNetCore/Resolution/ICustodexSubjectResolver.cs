using System.Security.Claims;

using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>Maps the authenticated principal to the Custodex subject a decision is evaluated for.</summary>
public interface ICustodexSubjectResolver
{
    /// <summary>Attempts to resolve a subject from <paramref name="user"/>.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="cancellationToken">A token that is cancelled when the request is aborted.</param>
    /// <returns>The resolved subject, or <see langword="null"/> to deny the request.</returns>
    ValueTask<SubjectRef?> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}
