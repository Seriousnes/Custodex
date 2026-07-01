using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// A DI-scoped decision cache the ASP.NET Core adapter consults around
/// <see cref="IAuthorizer.CheckAsync"/>. Its lifetime is one instance per Blazor circuit or per HTTP
/// request, so decisions are reused across the burst of checks a single render pass or request fires
/// without ever leaking a decision across scopes. Soundness comes from stamping each entry with the
/// <c>(schemaVersion, epoch)</c> it was computed under and validating that stamp against the current
/// values on every read; an <see cref="CheckRequest.Explain"/> request always evaluates live.
/// </summary>
public interface ICustodexDecisionCache
{
    /// <summary>
    /// Returns the decision for <paramref name="request"/>, reusing a cached entry when the schema
    /// version and tenant epoch it was computed under are still current, otherwise evaluating live and
    /// caching the fresh result. Concurrent identical checks share one evaluation.
    /// </summary>
    /// <param name="request">The check to decide.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The decision.</returns>
    Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default);

    /// <summary>Drops every cached decision in this scope.</summary>
    void Clear();

    /// <summary>Drops every cached decision about <paramref name="subject"/> in this scope.</summary>
    /// <param name="subject">The subject whose cached decisions are dropped.</param>
    void InvalidateSubject(SubjectRef subject);

    /// <summary>Drops every cached decision about <paramref name="obj"/> in this scope.</summary>
    /// <param name="obj">The object whose cached decisions are dropped.</param>
    void InvalidateObject(EntityRef obj);
}
