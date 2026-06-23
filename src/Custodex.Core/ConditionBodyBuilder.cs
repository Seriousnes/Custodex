using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core;

public sealed class ConditionBodyBuilder
{
    public ConditionExpr Param(string name) => new ParamRef(name);
    public ConditionExpr Attribute(string field) => new AttributeRef(field);
    public ConditionExpr Now() => new ContextNow();
    public ConditionExpr Subject() => new ContextSubject();

    public ConditionExpr Const(bool value) => new LiteralBool(value);
    public ConditionExpr Const(long value) => new LiteralInt(value);
    public ConditionExpr Const(double value) => new LiteralDouble(value);
    public ConditionExpr Const(string value) => new LiteralString(value);

    public ConditionExpr Hour(ConditionExpr timestamp) => new HourOf(timestamp);

    public ConditionExpr Eq(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Eq, r);
    public ConditionExpr Ne(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Ne, r);
    public ConditionExpr Lt(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Lt, r);
    public ConditionExpr Le(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Le, r);
    public ConditionExpr Gt(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Gt, r);
    public ConditionExpr Ge(ConditionExpr l, ConditionExpr r) => new Compare(l, CompareOp.Ge, r);

    public ConditionExpr And(ConditionExpr l, ConditionExpr r) => new BoolOp(l, BoolConnective.And, r);
    public ConditionExpr Or(ConditionExpr l, ConditionExpr r) => new BoolOp(l, BoolConnective.Or, r);
    public ConditionExpr Not(ConditionExpr inner) => new Not(inner);

    public ConditionExpr Add(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Add, r);
    public ConditionExpr Sub(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Sub, r);
    public ConditionExpr Mul(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Mul, r);
    public ConditionExpr Div(ConditionExpr l, ConditionExpr r) => new Arithmetic(l, ArithOp.Div, r);

    public ConditionExpr In(ConditionExpr item, params ConditionExpr[] items) => new InList(item, items);
}
