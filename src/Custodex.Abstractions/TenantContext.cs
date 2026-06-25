namespace Custodex.Abstractions;

/// <summary>
/// The isolation scope every storage operation and authorization query is bound to. A <c>store</c>
/// holds one active schema and its data; a <c>tenant</c> partitions that data so no query crosses
/// tenant boundaries. It is carried alongside operations rather than embedded in tuples, which keeps
/// tuple values reusable across stores.
/// </summary>
/// <param name="Store">The store identifier, selecting the active schema and its data set; compared ordinally.</param>
/// <param name="Tenant">The tenant identifier, partitioning data within the store; compared ordinally.</param>
public readonly record struct TenantContext(string Store, string Tenant);
