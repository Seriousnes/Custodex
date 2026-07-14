namespace Custodex.AspNetCore;

/// <summary>
/// Bounds the page size honored by the list operations. A caller may request a larger page, but the
/// service caps the effective size at <see cref="Max"/> so a single request cannot force an unbounded
/// per-request allocation. Bound from <c>Custodex:MaxPageSize</c>.
/// </summary>
public sealed class PageSizeOptions
{
    /// <summary>The upper bound applied when <c>Custodex:MaxPageSize</c> is not configured.</summary>
    public const int DefaultMax = 1000;

    /// <summary>The largest page size honored for list-objects and list-subjects. Defaults to <see cref="DefaultMax"/>.</summary>
    public int Max { get; set; } = DefaultMax;
}
