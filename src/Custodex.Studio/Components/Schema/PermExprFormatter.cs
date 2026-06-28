using Custodex.Abstractions;

namespace Custodex.Studio.Components.Schema;

/// <summary>
/// Renders a <see cref="PermExpr"/> as the readable DSL text used to author it: relation names verbatim,
/// the binary operators <c>+</c> (union), <c>&amp;</c> (intersection) and <c>-</c> (exclusion),
/// arrow traversal as <c>relation-&gt;permission</c>, and a gating condition as <c>expr with condition</c>.
/// A binary operand that is itself a binary expression is wrapped in parentheses so operator precedence
/// is never ambiguous (for example <c>a + (b &amp; c)</c>).
/// </summary>
public static class PermExprFormatter
{
    /// <summary>Renders <paramref name="expr"/> as readable DSL text.</summary>
    /// <param name="expr">The permission expression to render.</param>
    /// <returns>The formatted expression.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an unrecognised expression node is encountered.</exception>
    public static string Format(PermExpr expr) => expr switch
    {
        RelationRef r => r.Relation,
        Union u => $"{Operand(u.Left)} + {Operand(u.Right)}",
        Intersect i => $"{Operand(i.Left)} & {Operand(i.Right)}",
        Exclude e => $"{Operand(e.Left)} - {Operand(e.Right)}",
        Arrow a => $"{a.Relation}->{a.Permission}",
        Conditioned c => $"{Format(c.Inner)} with {c.ConditionName}",
        _ => throw new ArgumentOutOfRangeException(nameof(expr), expr, "Unrecognised permission expression node.")
    };

    private static string Operand(PermExpr expr) =>
        expr is Union or Intersect or Exclude ? $"({Format(expr)})" : Format(expr);
}
