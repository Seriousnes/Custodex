using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Process-wide factory for tenant-scoped <see cref="PostgresCacheStore"/> instances. Registered once
/// in DI; per-request code calls <see cref="For"/> to get a store hard-filtered to that tenant. All
/// stores produced by this factory read and write the shared <c>cache_entries</c> rows keyed by
/// <c>(store_id, tenant_id, key)</c>, so instances on different nodes share one cache.
/// </summary>
public sealed class PostgresCacheStoreFactory(string connectionString)
{
    /// <summary>Returns a new <see cref="ICacheStore"/> scoped to <paramref name="tenant"/>.</summary>
    public ICacheStore For(TenantContext tenant) => new PostgresCacheStore(connectionString, tenant);
}
