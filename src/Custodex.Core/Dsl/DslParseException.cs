namespace Custodex.Core.Dsl;

/// <summary>Thrown when the DSL text contains a lexical or syntactic error.</summary>
/// <remarks>Initializes a new <see cref="DslParseException"/> with a message and source position.</remarks>
public sealed class DslParseException(string message, int line, int column) : Exception($"{message} (line {line}, col {column})")
{
    /// <summary>The 1-based line number where the error occurred.</summary>
    public int Line { get; } = line;

    /// <summary>The 1-based column number where the error occurred.</summary>
    public int Column { get; } = column;
}
