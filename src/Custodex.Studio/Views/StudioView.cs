namespace Custodex.Studio.Views;

/// <summary>
/// An opaque piece of named console configuration persisted per (store, tenant). The engine knows
/// nothing about these rows; the console interprets <see cref="ConfigJson"/> according to
/// <see cref="Kind"/>.
/// </summary>
/// <param name="Store">The store the configuration belongs to.</param>
/// <param name="Tenant">The tenant the configuration belongs to.</param>
/// <param name="Owner">
/// The identity that owns the configuration. When no authenticated user is available the
/// console uses <see cref="SharedOwner"/> so the row is visible to every operator of the
/// (store, tenant); a real user identifier can populate this later without a contract change.
/// </param>
/// <param name="Kind">A category discriminator, letting one table hold several config shapes.</param>
/// <param name="Key">The name distinguishing one configuration of a given <see cref="Kind"/> from another.</param>
/// <param name="ConfigJson">The configuration payload, serialized as JSON and opaque to the store.</param>
/// <param name="UpdatedAt">The instant the configuration was last written, supplied by the caller.</param>
public sealed record StudioView(
    string Store,
    string Tenant,
    string Owner,
    string Kind,
    string Key,
    string ConfigJson,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The <see cref="Owner"/> value used when no authenticated user identity is available, so a
    /// saved configuration is shared across every operator of the (store, tenant).
    /// </summary>
    public const string SharedOwner = "shared";
}
