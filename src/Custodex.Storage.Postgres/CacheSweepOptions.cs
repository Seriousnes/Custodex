namespace Custodex.Storage.Postgres;

/// <summary>Configuration for the background <c>cache_entries</c> TTL sweep.</summary>
public sealed class CacheSweepOptions
{
    public required string ConnectionString { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);
}
