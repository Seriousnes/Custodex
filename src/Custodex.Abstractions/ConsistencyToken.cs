using System.Text;

namespace Custodex.Abstractions;

/// <summary>
/// The decoded contents of a <see cref="ConsistencyToken"/>: the tenant scope it was minted for and the
/// two monotonic positions it captures.
/// </summary>
/// <param name="Store">The store the token was minted for.</param>
/// <param name="Tenant">The tenant the token was minted for.</param>
/// <param name="Epoch">The cache epoch the minting write advanced to; a read served under this epoch or later reflects the write.</param>
/// <param name="ChangeLogId">The change-log position the minting write reached.</param>
public readonly record struct ConsistencyTokenParts(string Store, string Tenant, long Epoch, long ChangeLogId);

/// <summary>
/// An opaque marker of a point in a tenant's write history, returned by every write and replayed on a
/// later read to bound how stale a served decision may be. It composes the per-<c>(store, tenant)</c>
/// cache epoch and the monotonic change-log position of the write that produced it; both components are
/// non-decreasing across successive writes to the same tenant. The encoded value is opaque by
/// convention — a caller holds it and passes it back rather than parsing it — and is deterministic: the
/// same inputs always encode to the same value, with no clock or randomness involved.
/// </summary>
/// <param name="Value">The opaque, URL-safe encoded token.</param>
public sealed record ConsistencyToken(string Value)
{
    private const byte Version = 1;

    /// <summary>
    /// Mints a token capturing a write's tenant scope, post-write cache <paramref name="epoch"/>, and
    /// change-log position <paramref name="changeLogId"/>.
    /// </summary>
    /// <param name="tenant">The tenant scope the write committed under.</param>
    /// <param name="epoch">The cache epoch the write advanced the tenant to.</param>
    /// <param name="changeLogId">The change-log entry id the write reached, or zero when it recorded no entry.</param>
    /// <returns>An opaque token a caller can replay on a later read.</returns>
    public static ConsistencyToken Create(TenantContext tenant, long epoch, long changeLogId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(tenant.Store);
            writer.Write(tenant.Tenant);
            writer.Write(epoch);
            writer.Write(changeLogId);
        }

        var encoded = Convert.ToBase64String(buffer.ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new ConsistencyToken(encoded);
    }

    /// <summary>Decodes the token into its components.</summary>
    /// <returns>The tenant scope and the two positions the token captures.</returns>
    /// <exception cref="InvalidConsistencyTokenException">The value is empty, not well-formed, or carries an unsupported version marker.</exception>
    public ConsistencyTokenParts Decode()
    {
        if (string.IsNullOrEmpty(Value))
            throw new InvalidConsistencyTokenException("The token value is empty.");

        byte[] bytes;
        try
        {
            var padded = Value.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            throw new InvalidConsistencyTokenException("The token value is not valid base64url.");
        }

        try
        {
            using var buffer = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);

            var version = reader.ReadByte();
            if (version != Version)
                throw new InvalidConsistencyTokenException($"Unsupported token version {version}.");

            var store = reader.ReadString();
            var tenant = reader.ReadString();
            var epoch = reader.ReadInt64();
            var changeLogId = reader.ReadInt64();

            if (buffer.Position != buffer.Length)
                throw new InvalidConsistencyTokenException("The token value has trailing content.");

            return new ConsistencyTokenParts(store, tenant, epoch, changeLogId);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidConsistencyTokenException("The token value is truncated.");
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            throw new InvalidConsistencyTokenException("The token value is not well-formed.");
        }
    }

    /// <summary>Decodes the token, requires it to belong to <paramref name="tenant"/>, and returns its epoch.</summary>
    /// <param name="tenant">The tenant scope the token must match.</param>
    /// <returns>The cache epoch the token captures.</returns>
    /// <exception cref="InvalidConsistencyTokenException">The token is malformed, or its store and tenant do not match <paramref name="tenant"/>.</exception>
    public long EpochFor(TenantContext tenant)
    {
        var parts = Decode();
        if (!string.Equals(parts.Store, tenant.Store, StringComparison.Ordinal)
            || !string.Equals(parts.Tenant, tenant.Tenant, StringComparison.Ordinal))
            throw new InvalidConsistencyTokenException("The token belongs to a different tenant.");
        return parts.Epoch;
    }
}
