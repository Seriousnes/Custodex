using System.Text.Json.Serialization;

namespace Custodex.Abstractions;

/// <summary>
/// The comparison operator carried by a <see cref="Compare"/> node:
/// equal, not-equal, less-than, less-or-equal, greater-than, greater-or-equal.
/// </summary>
public enum CompareOp
{
    /// <summary>Equality (<c>==</c>): true when both operands compare equal.</summary>
    Eq,

    /// <summary>Inequality (<c>!=</c>): true when the operands do not compare equal.</summary>
    Ne,

    /// <summary>Less-than (<c>&lt;</c>): true when the left operand orders before the right.</summary>
    Lt,

    /// <summary>Less-than-or-equal (<c>&lt;=</c>): true when the left operand orders before or equal to the right.</summary>
    Le,

    /// <summary>Greater-than (<c>&gt;</c>): true when the left operand orders after the right.</summary>
    Gt,

    /// <summary>Greater-than-or-equal (<c>&gt;=</c>): true when the left operand orders after or equal to the right.</summary>
    Ge,
}

/// <summary>The boolean connective carried by a <see cref="BoolOp"/> node.</summary>
public enum BoolConnective
{
    /// <summary>Logical AND: true only when both operands evaluate true. Evaluation short-circuits when the left operand is false.</summary>
    And,

    /// <summary>Logical OR: true when either operand evaluates true. Evaluation short-circuits when the left operand is true.</summary>
    Or,
}

/// <summary>The arithmetic operator carried by an <see cref="Arithmetic"/> node.</summary>
public enum ArithOp
{
    /// <summary>Addition: the sum of the two operands.</summary>
    Add,

    /// <summary>Subtraction: the left operand minus the right.</summary>
    Sub,

    /// <summary>Multiplication: the product of the two operands.</summary>
    Mul,

    /// <summary>Division: the left operand divided by the right. Dividing by zero fails the condition (default-deny). The result is always a floating-point value, even when both operands are integers.</summary>
    Div,
}

/// <summary>The body assigned to a condition declared with parameters only and no predicate expression.</summary>
public sealed record EmptyConditionBody : ConditionExpr;

/// <summary>A constant boolean value.</summary>
/// <param name="Value">The literal evaluated as-is.</param>
public sealed record LiteralBool(bool Value) : ConditionExpr;

/// <summary>A constant integer value.</summary>
/// <param name="Value">The literal evaluated as-is.</param>
public sealed record LiteralInt(long Value) : ConditionExpr;

/// <summary>A constant floating-point value.</summary>
/// <param name="Value">The literal evaluated as-is.</param>
public sealed record LiteralDouble(double Value) : ConditionExpr;

/// <summary>A constant string value.</summary>
/// <param name="Value">The literal evaluated as-is.</param>
public sealed record LiteralString(string Value) : ConditionExpr;

/// <summary>
/// Reads the value bound to one of the condition's typed parameters, using that parameter's declared type.
/// </summary>
/// <param name="Name">The condition parameter name to resolve.</param>
public sealed record ParamRef(string Name) : ConditionExpr;

/// <summary>
/// Reads a named field from the request's attribute bag. Evaluation fails (default-deny) when the field is absent.
/// An optional declared type lets the field resolve as that type regardless of how the underlying store encoded the
/// value; in particular a declared <see cref="ConditionType.Timestamp"/> parses a string-encoded value to a timestamp,
/// so two encodings of the same instant compare chronologically rather than ordinally. When the type is omitted the
/// field resolves by the value's runtime type, exactly as an untyped read.
/// </summary>
/// <param name="Field">The attribute field name to look up.</param>
/// <param name="Type">
/// The declared type the field resolves as, or <see langword="null"/> to resolve by the value's runtime type. A
/// declared type that the stored value cannot satisfy fails the condition (default-deny).
/// </param>
public sealed record AttributeRef(
    string Field,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConditionType? Type = null) : ConditionExpr;

/// <summary>
/// The ambient request time, as a timestamp. Lets a condition compare against "now" deterministically.
/// </summary>
public sealed record ContextNow : ConditionExpr;

/// <summary>
/// The id of the subject the request is being decided for, as a string.
/// </summary>
public sealed record ContextSubject : ConditionExpr;

/// <summary>
/// Compares two operands and yields a boolean. Numeric operands are compared by value, strings ordinally, and
/// timestamps chronologically; booleans support only equality and inequality. Operands of incomparable kinds
/// fail the condition (default-deny).
/// </summary>
/// <param name="Left">The left operand.</param>
/// <param name="Op">The comparison applied to the operands.</param>
/// <param name="Right">The right operand.</param>
public sealed record Compare(ConditionExpr Left, CompareOp Op, ConditionExpr Right) : ConditionExpr;

/// <summary>
/// Combines two boolean operands with a logical connective, with short-circuit evaluation of the right operand.
/// </summary>
/// <param name="Left">The left boolean operand, always evaluated.</param>
/// <param name="Op">The connective applied: <see cref="BoolConnective.And"/> or <see cref="BoolConnective.Or"/>.</param>
/// <param name="Right">The right boolean operand, evaluated only when the left operand does not already decide the result.</param>
public sealed record BoolOp(ConditionExpr Left, BoolConnective Op, ConditionExpr Right) : ConditionExpr;

/// <summary>Logical negation: true when <paramref name="Inner"/> evaluates false, and vice versa.</summary>
/// <param name="Inner">The boolean operand to negate.</param>
public sealed record Not(ConditionExpr Inner) : ConditionExpr;

/// <summary>
/// Computes a numeric result from two numeric operands. Integer operands yield an integer result for
/// addition, subtraction, and multiplication; division always yields a floating-point result. Non-numeric
/// operands fail the condition (default-deny).
/// </summary>
/// <param name="Left">The left operand.</param>
/// <param name="Op">The arithmetic operation applied.</param>
/// <param name="Right">The right operand.</param>
public sealed record Arithmetic(ConditionExpr Left, ArithOp Op, ConditionExpr Right) : ConditionExpr;

/// <summary>
/// Membership test: true when <paramref name="Item"/> compares equal to any element of <paramref name="Items"/>,
/// using the same equality rules as <see cref="Compare"/>.
/// </summary>
/// <param name="Item">The value sought within the list.</param>
/// <param name="Items">The candidate elements to test for equality against <paramref name="Item"/>.</param>
public sealed record InList(ConditionExpr Item, IReadOnlyList<ConditionExpr> Items) : ConditionExpr;

/// <summary>
/// Extracts the hour-of-day (0&#8211;23) from a timestamp operand, as an integer. A non-timestamp operand
/// fails the condition (default-deny).
/// </summary>
/// <param name="Timestamp">The timestamp operand to read the hour from.</param>
public sealed record HourOf(ConditionExpr Timestamp) : ConditionExpr;
