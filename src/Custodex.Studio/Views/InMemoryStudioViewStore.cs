using System.Collections.Concurrent;

namespace Custodex.Studio.Views;

/// <summary>
/// A process-local <see cref="IStudioViewStore"/> that keeps saved configurations in memory. It is
/// the default registered by <c>AddCustodexStudio</c>, so the console works with no external
/// dependencies; saved views live only for the lifetime of the host. Substitute a durable store
/// (for example the Postgres view store) to persist views across restarts. Safe for the concurrent
/// access of many Blazor circuits.
/// </summary>
public sealed class InMemoryStudioViewStore : IStudioViewStore
{
    private readonly ConcurrentDictionary<(string Store, string Tenant, string Owner, string Kind, string Key), StudioView> _views = new();

    /// <inheritdoc />
    public Task SaveAsync(StudioView view, CancellationToken ct = default)
    {
        _views[(view.Store, view.Tenant, view.Owner, view.Kind, view.Key)] = view;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StudioView>> ListAsync(
        string store, string tenant, string? kind = null, CancellationToken ct = default)
    {
        IReadOnlyList<StudioView> result =
        [
            .. _views.Values
                .Where(v => string.Equals(v.Store, store, StringComparison.Ordinal)
                    && string.Equals(v.Tenant, tenant, StringComparison.Ordinal)
                    && (kind is null || string.Equals(v.Kind, kind, StringComparison.Ordinal)))
                .OrderByDescending(v => v.UpdatedAt)
        ];
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<StudioView?> GetAsync(
        string store, string tenant, string owner, string kind, string key, CancellationToken ct = default) =>
        Task.FromResult(_views.GetValueOrDefault((store, tenant, owner, kind, key)));

    /// <inheritdoc />
    public Task DeleteAsync(
        string store, string tenant, string owner, string kind, string key, CancellationToken ct = default)
    {
        _views.TryRemove((store, tenant, owner, kind, key), out _);
        return Task.CompletedTask;
    }
}
