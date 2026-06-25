using System.Text;

namespace Custodex.Core.Evaluation;

/// <summary>
/// Opaque, deterministic pagination cursor over a stable object-id ordering.
/// Encodes the last-returned object id; callers resume strictly after it.
/// </summary>
public static class ContinuationCursor
{
    /// <summary>Encodes the last-returned object id into an opaque continuation token.</summary>
    /// <param name="lastObjectId">The id of the final item on the page just returned.</param>
    /// <returns>A URL-safe token the caller passes back to resume after that id.</returns>
    public static string Encode(string lastObjectId)
    {
        var bytes = Encoding.UTF8.GetBytes(lastObjectId);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes a continuation token back to the object id to resume strictly after.</summary>
    /// <param name="token">A token from a previous <see cref="Encode"/>, or <see langword="null"/>/empty to start from the beginning.</param>
    /// <returns>The object id to resume after, or <see langword="null"/> when no token was supplied.</returns>
    public static string? DecodeAfter(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var padded = token.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
