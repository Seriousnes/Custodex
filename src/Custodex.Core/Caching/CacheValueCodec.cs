namespace Custodex.Core.Caching;

public static class CacheValueCodec
{
    public static byte[] Encode(bool allowed) => [allowed ? (byte)1 : (byte)0];
    public static bool Decode(byte[] value) => value.Length > 0 && value[0] == 1;
}
