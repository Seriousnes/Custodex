namespace Relkit.Core.Conditions;

public sealed record ConditionResult(bool Allowed, string? Diagnostic)
{
    public static readonly ConditionResult Allow = new(true, null);
    public static readonly ConditionResult Deny = new(false, null);
    public static ConditionResult Error(string diagnostic) => new(false, diagnostic);
}
