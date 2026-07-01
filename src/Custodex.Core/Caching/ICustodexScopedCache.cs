using Custodex.Abstractions;

namespace Custodex.Core.Caching;

/// <summary>
/// The invalidation seam of the scoped in-process decision cache. The scoped
/// <see cref="IAuthorizer"/> decorator implements this alongside <see cref="IAuthorizer"/>, and both
/// resolve to the same per-scope instance, so a host can drop cached decisions immediately after a
/// known write instead of waiting for the epoch to be re-read. Every method acts on the current scope
/// only and never leaks across scopes.
/// </summary>
public interface ICustodexScopedCache
{
    /// <summary>Drops every cached decision in this scope.</summary>
    void Clear();

    /// <summary>Drops every cached decision about <paramref name="subject"/> in this scope.</summary>
    /// <param name="subject">The subject whose cached decisions are dropped.</param>
    void InvalidateSubject(SubjectRef subject);

    /// <summary>Drops every cached decision about <paramref name="obj"/> in this scope.</summary>
    /// <param name="obj">The object whose cached decisions are dropped.</param>
    void InvalidateObject(EntityRef obj);
}
