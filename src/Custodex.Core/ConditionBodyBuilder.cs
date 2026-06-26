using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core;

/// <summary>
/// Fluent factory for the expression tree of a condition body. Leaf methods name a value (a parameter,
/// a request attribute, context, or a literal); the remaining methods compose those values into the
/// boolean predicate the condition evaluates to.
/// </summary>
public sealed class ConditionBodyBuilder
{
    /// <summary>References a declared condition parameter by name.</summary>
    /// <param name="name">The parameter name, as declared for the condition.</param>
    /// <returns>The parameter reference.</returns>    
    public ConditionExpr Param(string name) => new ParamRef(name);

    /// <summary>References a field of the request's resource attributes.</summary>
    /// <param name="field">The attribute field name to read.</param>
    /// <returns>The attribute reference.</returns>
    public ConditionExpr Attribute(string field) => new AttributeRef(field);

    /// <summary>References the request's current time supplied by the caller.</summary>
    /// <returns>The context-now reference.</returns>
    public ConditionExpr Now() => new ContextNow();

    /// <summary>References the subject of the request being evaluated.</summary>
    /// <returns>The context-subject reference.</returns>
    public ConditionExpr Subject() => new ContextSubject();

    /// <summary>A boolean literal.</summary>
    /// <param name="value">The literal value.</param>
    /// <returns>The literal expression.</returns>
    public ConditionExpr Const(bool value) => new LiteralBool(value);

    /// <summary>An integer literal.</summary>
    /// <param name="value">The literal value.</param>
    /// <returns>The literal expression.</returns>
    public ConditionExpr Const(long value) => new LiteralInt(value);

    /// <summary>A floating-point literal.</summary>
    /// <param name="value">The literal value.</param>
    /// <returns>The literal expression.</returns>
    public ConditionExpr Const(double value) => new LiteralDouble(value);

    /// <summary>A string literal.</summary>
    /// <param name="value">The literal value.</param>
    /// <returns>The literal expression.</returns>
    public ConditionExpr Const(string value) => new LiteralString(value);

    /// <summary>Extracts the hour-of-day (0&#8211;23) from a timestamp expression.</summary>
    /// <param name="timestamp">The timestamp-valued expression to read the hour from.</param>
    /// <returns>The hour-of-day expression.</returns>
    public ConditionExpr Hour(ConditionExpr timestamp) => new HourOf(timestamp);

    /// <summary>Equality comparison (<c>l == r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The comparison expression.</returns>
    public ConditionExpr Eq(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Eq, r);

    /// <summary>Inequality comparison (<c>l != r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The comparison expression.</returns>
    public ConditionExpr Ne(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Ne, r);

    /// <summary>Less-than comparison (<c>l &lt; r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The comparison expression.</returns>
    public ConditionExpr Lt(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Lt, r);

    /// <summary>Less-than-or-equal comparison (<c>l &lt;= r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The comparison expression.</returns>
    public ConditionExpr Le(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Le, r);

    /// <summary>Greater-than comparison (<c>l &gt; r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The comparison expression.</returns>
    public ConditionExpr Gt(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Gt, r);

    /// <summary>Greater-than-or-equal comparison (<c>l &gt;= r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The comparison expression.</returns>
    public ConditionExpr Ge(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Ge, r);

    /// <summary>Logical conjunction (<c>l &amp;&amp; r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The boolean expression.</returns>
    public ConditionExpr And(ConditionExpr l, ConditionExpr r) => new BoolOp(l, BoolConnective.And, r);

    /// <summary>Logical disjunction (<c>l || r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The boolean expression.</returns>
    public ConditionExpr Or(ConditionExpr l, ConditionExpr r) => new BoolOp(l, BoolConnective.Or, r);

    /// <summary>Logical negation (<c>!inner</c>).</summary>
    /// <param name="inner">The operand to negate.</param>
    /// <returns>The boolean expression.</returns>
    public ConditionExpr Not(ConditionExpr inner) => new Not(inner);

    /// <summary>Addition (<c>l + r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The arithmetic expression.</returns>
    public ConditionExpr Add(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Add, r);

    /// <summary>Subtraction (<c>l - r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The arithmetic expression.</returns>
    public ConditionExpr Sub(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Sub, r);

    /// <summary>Multiplication (<c>l * r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The arithmetic expression.</returns>
    public ConditionExpr Mul(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Mul, r);

    /// <summary>Division (<c>l / r</c>).</summary>
    /// <param name="l">The left operand.</param>
    /// <param name="r">The right operand.</param>
    /// <returns>The arithmetic expression.</returns>
    public ConditionExpr Div(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Div, r);

    /// <summary>Membership test: true when <paramref name="item"/> equals one of <paramref name="items"/>.</summary>
    /// <param name="item">The value to look for.</param>
    /// <param name="items">The candidate values to test against.</param>
    /// <returns>The membership expression.</returns>
    public ConditionExpr In(ConditionExpr item, params ConditionExpr[] items) => new InList(item, items);
}
