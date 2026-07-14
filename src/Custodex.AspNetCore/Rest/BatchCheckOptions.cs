namespace Custodex.AspNetCore;

/// <summary>
/// Bounds the number of items honored in a single batch-check request, so one request cannot
/// force an unbounded number of permission evaluations. Bound from <c>Custodex:MaxBatchItems</c>.
/// </summary>
public sealed class BatchCheckOptions
{
    /// <summary>The upper bound applied when <c>Custodex:MaxBatchItems</c> is not configured.</summary>
    public const int DefaultMaxItems = 1000;

    /// <summary>The largest number of items honored in a single batch-check request. Defaults to <see cref="DefaultMaxItems"/>.</summary>
    public int MaxItems { get; set; } = DefaultMaxItems;
}
