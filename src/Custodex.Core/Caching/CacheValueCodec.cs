namespace Custodex.Core.Caching;

/// <summary>Serializes a cached allow/deny decision to and from its stored byte form.</summary>
public static class CacheValueCodec
{
    /// <summary>Encodes a decision for storage.</summary>
    /// <param name="allowed">The decision to store.</param>
    /// <returns>The stored representation of the decision.</returns>
    public static byte[] Encode(bool allowed) => [allowed ? (byte)1 : (byte)0];

    /// <summary>Decodes a stored decision. An empty value decodes as deny.</summary>
    /// <param name="value">The stored representation produced by <see cref="Encode"/>.</param>
    /// <returns>The decoded allow/deny decision.</returns>
    public static bool Decode(byte[] value) => value.Length > 0 && value[0] == 1;
}
