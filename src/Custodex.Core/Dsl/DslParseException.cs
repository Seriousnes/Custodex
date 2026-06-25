namespace Custodex.Core.Dsl;

/// <summary>Thrown when the DSL text contains a lexical or syntactic error.</summary>
public sealed class DslParseException : Exception
{
    /// <summary>The 1-based line number where the error occurred.</summary>
    public int Line { get; }

    /// <summary>The 1-based column number where the error occurred.</summary>
    public int Column { get; }

    /// <summary>Initializes a new <see cref="DslParseException"/> with a message and source position.</summary>
    public DslParseException(string message, int line, int column)
        : base($"{message} (line {line}, col {column})")
    {
        Line = line;
        Column = column;
    }
}
