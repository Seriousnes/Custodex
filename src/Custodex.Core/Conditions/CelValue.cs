namespace Custodex.Core.Conditions;

internal enum CelKind { Bool, Int, Double, String, Timestamp, Unknown }

internal sealed class CelValue
{
    public CelKind Kind { get; }
    private readonly bool _bool;
    private readonly long _long;
    private readonly double _double;
    private readonly string _string;
    private readonly DateTimeOffset _timestamp;
    private readonly IReadOnlyList<string> _missingKeys;

    private CelValue(CelKind kind, bool b = false, long l = 0, double d = 0,
        string s = "", DateTimeOffset ts = default, IReadOnlyList<string>? missingKeys = null)
    {
        Kind = kind; _bool = b; _long = l; _double = d; _string = s; _timestamp = ts;
        _missingKeys = missingKeys ?? [];
    }

    public static CelValue Bool(bool value) => new(CelKind.Bool, b: value);
    public static CelValue Int(long value) => new(CelKind.Int, l: value);
    public static CelValue Double(double value) => new(CelKind.Double, d: value);
    public static CelValue String(string value) => new(CelKind.String, s: value);
    public static CelValue Timestamp(DateTimeOffset value) => new(CelKind.Timestamp, ts: value);
    public static CelValue Unknown(IReadOnlyList<string> missingKeys) => new(CelKind.Unknown, missingKeys: missingKeys);

    public bool AsBool() => _bool;
    public long AsLong() => _long;
    public double AsDouble() => Kind == CelKind.Int ? _long : _double;
    public string AsString() => _string;
    public DateTimeOffset AsTimestamp() => _timestamp;
    public IReadOnlyList<string> MissingKeys => _missingKeys;

    public bool IsNumeric => Kind is CelKind.Int or CelKind.Double;
    public bool IsUnknown => Kind == CelKind.Unknown;
}
