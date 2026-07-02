namespace Custodex.Abstractions;

/// <summary>The consistency level a read requests, trading freshness against latency.</summary>
public enum ConsistencyMode
{
    /// <summary>Serve the fastest available answer, including a cached decision, without a freshness floor.</summary>
    MinimizeLatency = 0,

    /// <summary>Serve a cached decision only when it is at least as fresh as a supplied <see cref="ConsistencyToken"/>.</summary>
    AtLeastAsFresh = 1,

    /// <summary>Compute the decision against current store state, never serving a cached decision.</summary>
    FullyConsistent = 2,
}

/// <summary>
/// The consistency level carried on a read. It governs whether a cached decision may be served: the
/// engine always reads the single source-of-truth store, so store reads are current by construction, and
/// this selector bounds only how stale a <em>cached</em> decision the caller will accept.
/// </summary>
/// <param name="Mode">The requested consistency level.</param>
/// <param name="Token">The freshness floor for <see cref="ConsistencyMode.AtLeastAsFresh"/>; otherwise <see langword="null"/>.</param>
public sealed record Consistency(ConsistencyMode Mode, ConsistencyToken? Token = null)
{
    /// <summary>Serve the fastest available answer, including a cached decision. This is the default when no consistency is requested.</summary>
    public static Consistency MinimizeLatency { get; } = new(ConsistencyMode.MinimizeLatency);

    /// <summary>Compute the decision fresh against current store state, never serving a cached decision.</summary>
    public static Consistency FullyConsistent { get; } = new(ConsistencyMode.FullyConsistent);

    /// <summary>
    /// Serve a cached decision only when it is at least as fresh as <paramref name="token"/> — the token
    /// returned by an earlier write — otherwise recompute. This gives read-your-writes: replay the token
    /// from your last write and the read reflects it.
    /// </summary>
    /// <param name="token">The token from a prior write that the served decision must be at least as fresh as.</param>
    /// <returns>A selector requiring at-least-as-fresh consistency against <paramref name="token"/>.</returns>
    public static Consistency AtLeastAsFresh(ConsistencyToken token) =>
        new(ConsistencyMode.AtLeastAsFresh, token);
}
