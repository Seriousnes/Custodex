using System.Text;

using Bogus;

namespace Custodex.TestKit;

public sealed class NeutralIdentifiers : DataSet
{
    private readonly Bogus.DataSets.Hacker _hacker = new();
    private readonly Bogus.DataSets.Lorem _lorem = new();

    public NeutralIdentifiers(Randomizer randomizer)
    {
        Random = randomizer;
        _hacker.Random = randomizer;
        _lorem.Random = randomizer;
    }

    public string Noun() => Sanitize(_hacker.Noun());

    public string Verb() => Sanitize(_hacker.Verb());

    public string Adjective() => Sanitize(_hacker.Adjective());

    public string Word() => Sanitize(_lorem.Word());

    public string Token(int length = 8) => Random.AlphaNumeric(length).ToLowerInvariant();

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
