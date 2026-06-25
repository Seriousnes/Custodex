namespace Custodex.Storage.Postgres;

/// <summary>Configuration for the background <c>cache_entries</c> TTL sweep.</summary>
public sealed class CacheSweepOptions
{
    /// <summary>The Postgres connection string the sweep runs its reclaim query on.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>How often the sweep runs; defaults to five minutes.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);
}
