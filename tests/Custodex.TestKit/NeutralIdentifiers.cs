using System.Text;
using Bogus;

namespace Custodex.TestKit;

/// <summary>
/// A Bogus <see cref="DataSet"/> that vends industry-neutral, identifier-safe tokens for
/// authorization vocabulary (entity types, relations, permissions, ids, condition/param names).
/// </summary>
/// <remarks>
/// Draws only from de-domained Bogus generators (<see cref="Bogus.DataSets.Hacker"/>,
/// <see cref="Bogus.DataSets.Lorem"/>, <see cref="Bogus.Randomizer"/>) so that nothing in the
/// output implies a specific industry. All output is lowercased and reduced to the
/// <c>[a-z0-9_-]</c> character class. The nested datasets share the supplied
/// <see cref="Randomizer"/>, so output is deterministic for a given seed.
/// </remarks>
public sealed class NeutralIdentifiers : DataSet
{
    private readonly Bogus.DataSets.Hacker _hacker = new();
    private readonly Bogus.DataSets.Lorem _lorem = new();

    /// <summary>
    /// Creates the dataset bound to <paramref name="randomizer"/>. The same instance drives this dataset
    /// and the nested <see cref="Bogus.DataSets.Hacker"/>/<see cref="Bogus.DataSets.Lorem"/>, so a single
    /// seed produces a deterministic sequence across every generator.
    /// </summary>
    public NeutralIdentifiers(Randomizer randomizer)
    {
        Random = randomizer;
        _hacker.Random = randomizer;
        _lorem.Random = randomizer;
    }

    /// <summary>A single neutral noun token (e.g. an entity type like <c>resource</c>).</summary>
    public string Noun() => Sanitize(_hacker.Noun());

    /// <summary>A single neutral verb token (e.g. a relation or permission like <c>view</c>).</summary>
    public string Verb() => Sanitize(_hacker.Verb());

    /// <summary>A single neutral adjective token (useful for condition/param names).</summary>
    public string Adjective() => Sanitize(_hacker.Adjective());

    /// <summary>A single neutral lorem word token.</summary>
    public string Word() => Sanitize(_lorem.Word());

    /// <summary>A short opaque identifier suffix of lowercase letters and digits.</summary>
    public string Token(int length = 8) => Random.AlphaNumeric(length).ToLowerInvariant();

    /// <summary>
    /// Reduces an arbitrary generated string to a lowercase, identifier-safe token:
    /// letters/digits pass through, runs of other characters collapse to a single <c>-</c>,
    /// and leading/trailing separators are trimmed. Falls back to a token if nothing survives.
    /// </summary>
    private string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return sb.Length > 0 ? sb.ToString() : Token();
    }
}
