using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Decorator over <see cref="NpgsqlCteAuthorizer"/> that routes <see cref="ListObjectsAsync"/>
/// through the maintained <c>reverse_index</c> (conditioned rows re-checked), falling back to the
/// inner CTE path when the index has no rows for the active schema version.
/// <see cref="CheckAsync"/>, <see cref="BatchCheckAsync"/>, and <see cref="ListSubjectsAsync"/>
/// delegate to the inner authorizer unchanged.
/// </summary>
/// <remarks>
/// Initializes the decorator with the inner CTE authorizer, the index store, and the schema store.
/// </remarks>
public sealed partial class IndexedAuthorizer(NpgsqlCteAuthorizer inner, IIndexStore index, ISchemaStore schemaStore) : IAuthorizer
{
    private readonly NpgsqlCteAuthorizer _inner = inner;
    private readonly IIndexStore _index = index;
    private readonly ISchemaStore _schemaStore = schemaStore;

    /// <inheritdoc />
    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
        => _inner.CheckAsync(request, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => _inner.BatchCheckAsync(request, ct);

    /// <inheritdoc />
    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default)
        => _inner.ListSubjectsAsync(request, ct);
}
