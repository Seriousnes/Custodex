namespace Custodex.Studio.Views;

/// <summary>
/// Persists opaque, categorized console configuration rows for the operator console. Every
/// operation is scoped to a (store, tenant); the store never inspects the configuration payload.
/// </summary>
public interface IStudioViewStore
{
    /// <summary>
    /// Inserts or updates a configuration row keyed by
    /// (<see cref="StudioView.Store"/>, <see cref="StudioView.Tenant"/>, <see cref="StudioView.Owner"/>,
    /// <see cref="StudioView.Kind"/>, <see cref="StudioView.Key"/>), recording the supplied
    /// <see cref="StudioView.UpdatedAt"/>.
    /// </summary>
    /// <param name="view">The configuration to persist.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SaveAsync(StudioView view, CancellationToken ct = default);

    /// <summary>
    /// Lists the configurations stored for a (store, tenant), most-recently-updated first,
    /// optionally restricted to a single <see cref="StudioView.Kind"/>.
    /// </summary>
    /// <param name="store">The store to list configurations for.</param>
    /// <param name="tenant">The tenant to list configurations for.</param>
    /// <param name="kind">When supplied, restricts the result to configurations of this kind.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The matching configurations, ordered most-recently-updated first.</returns>
    Task<IReadOnlyList<StudioView>> ListAsync(string store, string tenant, string? kind = null, CancellationToken ct = default);

    /// <summary>Reads a single configuration by its full key, or <see langword="null"/> when none exists.</summary>
    /// <param name="store">The store the configuration belongs to.</param>
    /// <param name="tenant">The tenant the configuration belongs to.</param>
    /// <param name="owner">The owner of the configuration.</param>
    /// <param name="kind">The kind of the configuration.</param>
    /// <param name="key">The name of the configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The configuration, or <see langword="null"/> when no row matches.</returns>
    Task<StudioView?> GetAsync(string store, string tenant, string owner, string kind, string key, CancellationToken ct = default);

    /// <summary>Removes a single configuration by its full key. Does nothing when no row matches.</summary>
    /// <param name="store">The store the configuration belongs to.</param>
    /// <param name="tenant">The tenant the configuration belongs to.</param>
    /// <param name="owner">The owner of the configuration.</param>
    /// <param name="kind">The kind of the configuration.</param>
    /// <param name="key">The name of the configuration.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task DeleteAsync(string store, string tenant, string owner, string kind, string key, CancellationToken ct = default);
}
