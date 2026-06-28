using Custodex.Abstractions;

namespace Custodex.Studio;

/// <summary>
/// Holds the store and tenant an operator selected for the current console session and notifies
/// components when that selection changes. Components read <see cref="Current"/> to scope the
/// Custodex management interfaces they inject to the selected tenant.
/// </summary>
public sealed class StudioConnectionState
{
    private string? _store;
    private string? _tenant;

    /// <summary>The selected store identifier, or <see langword="null"/> when none is selected.</summary>
    public string? Store => _store;

    /// <summary>The selected tenant identifier, or <see langword="null"/> when none is selected.</summary>
    public string? Tenant => _tenant;

    /// <summary>Whether both a store and a tenant are currently selected.</summary>
    public bool IsConnected => !string.IsNullOrEmpty(_store) && !string.IsNullOrEmpty(_tenant);

    /// <summary>The selected scope as a <see cref="TenantContext"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no store and tenant are selected.</exception>
    public TenantContext Current =>
        IsConnected
            ? new TenantContext(_store!, _tenant!)
            : throw new InvalidOperationException("Select a store and tenant before reading the connection scope.");

    /// <summary>Raised after the selected store or tenant changes so subscribed components can re-render.</summary>
    public event Action? Changed;

    /// <summary>Selects a store and tenant and raises <see cref="Changed"/>.</summary>
    /// <param name="store">The store identifier to select.</param>
    /// <param name="tenant">The tenant identifier to select.</param>
    public void Connect(string store, string tenant)
    {
        _store = store;
        _tenant = tenant;
        Changed?.Invoke();
    }

    /// <summary>Clears the selected store and tenant and raises <see cref="Changed"/>.</summary>
    public void Disconnect()
    {
        _store = null;
        _tenant = null;
        Changed?.Invoke();
    }
}
