namespace Custodex.Service;

/// <summary>
/// Configures the fixed-window rate limit applied per authenticated caller, so a single caller
/// cannot issue unbounded concurrent requests against the shared engine. Bound from
/// <c>Custodex:RateLimit:PermitLimit</c> and <c>Custodex:RateLimit:WindowSeconds</c>.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>The permit count applied when <c>Custodex:RateLimit:PermitLimit</c> is not configured.</summary>
    public const int DefaultPermitLimit = 1000;

    /// <summary>The window length in seconds applied when <c>Custodex:RateLimit:WindowSeconds</c> is not configured.</summary>
    public const int DefaultWindowSeconds = 1;

    /// <summary>The number of requests a caller may issue per window. Defaults to <see cref="DefaultPermitLimit"/>.</summary>
    public int PermitLimit { get; set; } = DefaultPermitLimit;

    /// <summary>The length of the fixed window, in seconds. Defaults to <see cref="DefaultWindowSeconds"/>.</summary>
    public int WindowSeconds { get; set; } = DefaultWindowSeconds;
}
