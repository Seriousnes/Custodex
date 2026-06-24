using Custodex.Abstractions;

namespace Custodex.Core.Conditions;

public enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge }
public enum BoolConnective { And, Or }
public enum ArithOp { Add, Sub, Mul, Div }

public sealed record LiteralBool(bool Value) : ConditionExpr;
public sealed record LiteralInt(long Value) : ConditionExpr;
public sealed record LiteralDouble(double Value) : ConditionExpr;
public sealed record LiteralString(string Value) : ConditionExpr;

public sealed record ParamRef(string Name) : ConditionExpr;
public sealed record AttributeRef(string Field) : ConditionExpr;      // resource[field]
public sealed record ContextNow : ConditionExpr;                      // context.now
public sealed record ContextSubject : ConditionExpr;                  // context.subject

public sealed record Compare(ConditionExpr Left, CompareOp Op, ConditionExpr Right) : ConditionExpr;
public sealed record BoolOp(ConditionExpr Left, BoolConnective Op, ConditionExpr Right) : ConditionExpr;
public sealed record Not(ConditionExpr Inner) : ConditionExpr;
public sealed record Arithmetic(ConditionExpr Left, ArithOp Op, ConditionExpr Right) : ConditionExpr;
public sealed record InList(ConditionExpr Item, IReadOnlyList<ConditionExpr> Items) : ConditionExpr;
public sealed record HourOf(ConditionExpr Timestamp) : ConditionExpr; // date-time helper: hour-of-day
