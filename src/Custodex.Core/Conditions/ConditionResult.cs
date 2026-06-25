namespace Custodex.Core.Conditions;

/// <summary>
/// The outcome of evaluating a condition: the allow/deny decision plus an optional diagnostic
/// explaining a denial. A failure or missing attribute is reported as a denial with a diagnostic,
/// never as an exception.
/// </summary>
/// <param name="Allowed">True when the condition held.</param>
/// <param name="Diagnostic">A human-readable explanation of a denial, or <see langword="null"/>.</param>
public sealed record ConditionResult(bool Allowed, string? Diagnostic)
{
    /// <summary>A clean allow with no diagnostic.</summary>
    public static readonly ConditionResult Allow = new(true, null);

    /// <summary>A clean deny with no diagnostic.</summary>
    public static readonly ConditionResult Deny = new(false, null);

    /// <summary>A deny carrying a diagnostic that explains why the condition could not be satisfied.</summary>
    public static ConditionResult Error(string diagnostic) => new(false, diagnostic);
}
