using System.Buffers.Text;
using System.Text;

namespace Relkit.Core.Evaluation;

/// <summary>
/// Opaque, deterministic pagination cursor over a stable object-id ordering.
/// Encodes the last-returned object id; callers resume strictly after it.
/// </summary>
public static class ContinuationCursor
{
    public static string Encode(string lastObjectId)
    {
        var bytes = Encoding.UTF8.GetBytes(lastObjectId);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? DecodeAfter(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var padded = token.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
