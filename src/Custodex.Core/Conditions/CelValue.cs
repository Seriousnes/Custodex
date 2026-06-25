namespace Custodex.Core.Conditions;

/// <summary>The runtime type of a <see cref="CelValue"/> computed during condition evaluation.</summary>
public enum CelKind
{
    /// <summary>A boolean.</summary>
    Bool,
    /// <summary>A 64-bit signed integer.</summary>
    Int,
    /// <summary>A double-precision floating-point number.</summary>
    Double,
    /// <summary>A string, compared ordinally.</summary>
    String,
    /// <summary>A point in time.</summary>
    Timestamp,
}

/// <summary>
/// A typed value produced while evaluating a condition expression. Integers and doubles
/// compare and combine as numbers; a condition body must ultimately reduce to a <see cref="CelKind.Bool"/>.
/// </summary>
public sealed class CelValue
{
    /// <summary>The runtime type this value holds.</summary>
    public CelKind Kind { get; }
    private readonly bool _bool;
    private readonly long _long;
    private readonly double _double;
    private readonly string _string;
    private readonly DateTimeOffset _timestamp;

    private CelValue(CelKind kind, bool b = false, long l = 0, double d = 0,
        string s = "", DateTimeOffset ts = default)
    {
        Kind = kind; _bool = b; _long = l; _double = d; _string = s; _timestamp = ts;
    }

    /// <summary>Creates a boolean value.</summary>
    public static CelValue Bool(bool value) => new(CelKind.Bool, b: value);

    /// <summary>Creates an integer value.</summary>
    public static CelValue Int(long value) => new(CelKind.Int, l: value);

    /// <summary>Creates a double value.</summary>
    public static CelValue Double(double value) => new(CelKind.Double, d: value);

    /// <summary>Creates a string value.</summary>
    public static CelValue String(string value) => new(CelKind.String, s: value);

    /// <summary>Creates a timestamp value.</summary>
    public static CelValue Timestamp(DateTimeOffset value) => new(CelKind.Timestamp, ts: value);

    /// <summary>Reads the underlying boolean. Meaningful only when <see cref="Kind"/> is <see cref="CelKind.Bool"/>.</summary>
    public bool AsBool() => _bool;

    /// <summary>Reads the underlying integer. Meaningful only when <see cref="Kind"/> is <see cref="CelKind.Int"/>.</summary>
    public long AsLong() => _long;

    /// <summary>Reads the value as a double, widening an <see cref="CelKind.Int"/> when needed for numeric comparison.</summary>
    public double AsDouble() => Kind == CelKind.Int ? _long : _double;

    /// <summary>Reads the underlying string. Meaningful only when <see cref="Kind"/> is <see cref="CelKind.String"/>.</summary>
    public string AsString() => _string;

    /// <summary>Reads the underlying timestamp. Meaningful only when <see cref="Kind"/> is <see cref="CelKind.Timestamp"/>.</summary>
    public DateTimeOffset AsTimestamp() => _timestamp;

    /// <summary>True when this value is an <see cref="CelKind.Int"/> or <see cref="CelKind.Double"/>.</summary>
    public bool IsNumeric => Kind is CelKind.Int or CelKind.Double;
}
