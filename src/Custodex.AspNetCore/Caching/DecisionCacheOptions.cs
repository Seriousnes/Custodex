namespace Custodex.AspNetCore;

/// <summary>
/// How the scoped in-process decision cache treats decisions drawn from a schema that declares
/// conditions (ABAC). A conditioned decision can vary with the request context (time and attributes),
/// so it is only safe to reuse a cached decision when the context is folded into the key.
/// </summary>
public enum ConditionedCaching
{
    /// <summary>Never cache a decision evaluated against a conditioned schema; every check is live.</summary>
    Skip,

    /// <summary>
    /// Cache conditioned decisions under a key that fingerprints the request context, so a decision is
    /// only reused when the time and attributes it was computed under match exactly.
    /// </summary>
    ContextInKey
}

/// <summary>
/// Tunes the scoped in-process authorization-decision cache the ASP.NET Core adapter consults around
/// <see cref="Custodex.Abstractions.IAuthorizer.CheckAsync"/>. The cache is sound by construction: every
/// entry is stamped with the <c>(schemaVersion, epoch)</c> it was computed under and re-evaluated when
/// either advances, so a cross-scope grant, deny, revocation, schema republish, or structural reach
/// change invalidates stale entries automatically.
/// </summary>
public sealed class DecisionCacheOptions
{
    /// <summary>Whether the decision cache is active. When <see langword="false"/>, every check evaluates live. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A hard staleness ceiling on every cached entry, on top of epoch and schema-version validation.
    /// Defaults to two minutes.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How decisions from a conditioned (ABAC) schema are cached. Defaults to <see cref="ConditionedCaching.Skip"/>.</summary>
    public ConditionedCaching Conditioned { get; set; } = ConditionedCaching.Skip;

    /// <summary>
    /// How long the per-scope snapshot of the active schema version and the tenant epoch is reused before
    /// it is refreshed from <see cref="Custodex.Abstractions.ISchemaStore"/> and
    /// <see cref="Custodex.Abstractions.ICacheStore"/>. This throttles store reads to roughly one per
    /// interval per tenant so a render burst does not fan out a round-trip per check. It also bounds the
    /// window in which a write in another scope can go unobserved within one long-lived scope (for example
    /// a Blazor circuit) to at most this interval; a shorter-lived scope such as an HTTP request never
    /// outlives it. Defaults to five seconds. Zero refreshes on every check.
    /// </summary>
    public TimeSpan EpochRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);
}
