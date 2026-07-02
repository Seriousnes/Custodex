namespace Custodex.Core.Conditions;

/// <summary>How a condition resolved against the available context.</summary>
public enum ConditionResolution
{
    /// <summary>The condition evaluated to false, or failed with complete context (a type mismatch, a missing parameter, a malformed body). Default-deny.</summary>
    Unsatisfied,

    /// <summary>The condition evaluated to true.</summary>
    Satisfied,

    /// <summary>The condition could not be resolved because attribute keys it reads were absent from both the object's attributes and the request context. Supplying them could change the outcome.</summary>
    MissingContext,
}

/// <summary>
/// The outcome of evaluating a condition: how it resolved, an optional diagnostic, and — when it could not be
/// resolved for want of context — the attribute keys that were missing. A failure with complete context (type
/// mismatch, missing parameter, malformed body) is reported as <see cref="ConditionResolution.Unsatisfied"/> with
/// a diagnostic, never as an exception.
/// </summary>
/// <param name="Resolution">How the condition resolved.</param>
/// <param name="Diagnostic">A human-readable explanation of a denial or missing context, or <see langword="null"/>.</param>
/// <param name="MissingKeys">The attribute keys that were absent when <see cref="Resolution"/> is <see cref="ConditionResolution.MissingContext"/>, in ordinal order; otherwise empty.</param>
public sealed record ConditionResult(ConditionResolution Resolution, string? Diagnostic, IReadOnlyList<string> MissingKeys)
{
    /// <summary>Whether the condition held.</summary>
    public bool Allowed => Resolution == ConditionResolution.Satisfied;

    /// <summary>A clean allow with no diagnostic.</summary>
    public static readonly ConditionResult Allow = new(ConditionResolution.Satisfied, null, []);

    /// <summary>A clean deny with no diagnostic.</summary>
    public static readonly ConditionResult Deny = new(ConditionResolution.Unsatisfied, null, []);

    /// <summary>A deny carrying a diagnostic that explains why the condition could not be satisfied with complete context.</summary>
    /// <param name="diagnostic">The explanation of the denial.</param>
    public static ConditionResult Error(string diagnostic) => new(ConditionResolution.Unsatisfied, diagnostic, []);

    /// <summary>A result that could not be resolved because required context was absent.</summary>
    /// <param name="missingKeys">The attribute keys that were missing, in ordinal order.</param>
    /// <param name="diagnostic">The explanation of the missing context.</param>
    public static ConditionResult Missing(IReadOnlyList<string> missingKeys, string? diagnostic) =>
        new(ConditionResolution.MissingContext, diagnostic, missingKeys);
}
