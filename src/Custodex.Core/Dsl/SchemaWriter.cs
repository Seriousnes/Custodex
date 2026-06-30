using System.Globalization;
using System.Text;

using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core.Dsl;

/// <summary>Serializes a <see cref="Schema"/> to its DSL text representation.</summary>
public static class SchemaWriter
{
    /// <summary>Returns the DSL text for the given schema. Round-trips through <see cref="Parsing.SchemaParser.Parse(string, string)"/>.</summary>
    /// <param name="schema">The schema to serialize.</param>
    /// <returns>The schema rendered as DSL source text.</returns>
    public static string Write(Schema schema)
    {
        var sb = new StringBuilder();

        foreach (var type in schema.Types)
            WriteType(sb, type);

        foreach (var cond in schema.Conditions)
            WriteCondition(sb, cond);

        return sb.ToString().TrimEnd();
    }

    private static void WriteType(StringBuilder sb, EntityTypeDef type)
    {
        sb.AppendLine($"type {type.Name} {{");

        foreach (var rel in type.Relations)
            WriteRelation(sb, rel);

        foreach (var perm in type.Permissions)
            WritePermission(sb, perm);

        sb.AppendLine("}");
    }

    private static void WriteRelation(StringBuilder sb, RelationDef rel)
    {
        sb.Append($"    relation {rel.Name}: ");
        sb.AppendLine(string.Join(" | ", rel.AllowedSubjects.Select(WriteSubject)));
    }

    private static string WriteSubject(SubjectTypeRef s)
    {
        if (s.Wildcard) return $"{s.Type}:*";
        if (s.Relation is not null) return $"{s.Type}#{s.Relation}";
        return s.Type;
    }

    private static void WritePermission(StringBuilder sb, PermissionDef perm)
    {
        sb.AppendLine($"    permission {perm.Name} = {WritePermExpr(perm.Expression, isRightChild: false)}");
    }

    private static string WritePermExpr(PermExpr expr, bool isRightChild)
    {
        var text = expr switch
        {
            RelationRef r => r.Relation,
            Arrow a => $"{a.Relation}->{a.Permission}",
            Union u => $"{WritePermExpr(u.Left, false)} + {WritePermExpr(u.Right, true)}",
            Intersect i => $"{WritePermExpr(i.Left, false)} & {WritePermExpr(i.Right, true)}",
            Exclude e => $"{WritePermExpr(e.Left, false)} - {WritePermExpr(e.Right, true)}",
            Conditioned c => WriteConditioned(c),
            _ => throw new InvalidOperationException($"Unknown PermExpr type: {expr.GetType().Name}"),
        };

        if (isRightChild && expr is Union or Intersect or Exclude)
            return $"({text})";

        return text;
    }

    private static string WriteConditioned(Conditioned c)
    {
        var inner = c.Inner is Union or Intersect or Exclude
            ? $"({WritePermExpr(c.Inner, false)})"
            : WritePermExpr(c.Inner, false);
        return $"{inner} with {c.ConditionName}";
    }

    private static void WriteCondition(StringBuilder sb, ConditionDef cond)
    {
        var paramList = string.Join(", ", cond.Parameters.Select(p => $"{p.Name}: {WriteCondType(p.Type)}"));
        if (cond.Body is EmptyConditionBody)
        {
            sb.AppendLine($"condition {cond.Name}({paramList})");
        }
        else
        {
            sb.AppendLine($"condition {cond.Name}({paramList}) = {WriteCondExpr(cond.Body)}");
        }
    }

    private static string WriteCondType(ConditionType t) => t switch
    {
        ConditionType.Bool => "bool",
        ConditionType.Int => "int",
        ConditionType.Long => "long",
        ConditionType.Double => "double",
        ConditionType.String => "string",
        ConditionType.Timestamp => "timestamp",
        _ => throw new InvalidOperationException($"Unknown ConditionType: {t}"),
    };

    private static string WriteCondExpr(ConditionExpr expr) => expr switch
    {
        LiteralBool b => b.Value ? "true" : "false",
        LiteralInt i => i.Value.ToString(CultureInfo.InvariantCulture),
        LiteralDouble d => d.Value.ToString("G", CultureInfo.InvariantCulture).Contains('.')
            ? d.Value.ToString("G", CultureInfo.InvariantCulture)
            : d.Value.ToString("G", CultureInfo.InvariantCulture) + ".0",
        LiteralString s => $"\"{s.Value}\"",
        ParamRef p => p.Name,
        AttributeRef { Type: ConditionType.Timestamp } a => $"timestamp(resource[\"{a.Field}\"])",
        AttributeRef a => $"resource[\"{a.Field}\"]",
        ContextNow => "context.now",
        ContextSubject => "context.subject",
        HourOf h => $"hour({WriteCondExpr(h.Timestamp)})",
        InList il => $"{WriteCondExpr(il.Item)} in ({string.Join(", ", il.Items.Select(WriteCondExpr))})",
        Not n => $"!({WriteCondExpr(n.Inner)})",
        Compare c => $"({WriteCondExpr(c.Left)} {WriteCompareOp(c.Op)} {WriteCondExpr(c.Right)})",
        BoolOp b => $"({WriteCondExpr(b.Left)} {WriteBoolConnective(b.Op)} {WriteCondExpr(b.Right)})",
        Arithmetic a => $"({WriteCondExpr(a.Left)} {WriteArithOp(a.Op)} {WriteCondExpr(a.Right)})",
        EmptyConditionBody => throw new InvalidOperationException("Cannot serialize EmptyConditionBody as an expression."),
        _ => throw new InvalidOperationException($"Unknown ConditionExpr type: {expr.GetType().Name}"),
    };

    private static string WriteCompareOp(CompareOp op) => op switch
    {
        CompareOp.Eq => "==",
        CompareOp.Ne => "!=",
        CompareOp.Lt => "<",
        CompareOp.Le => "<=",
        CompareOp.Gt => ">",
        CompareOp.Ge => ">=",
        _ => throw new InvalidOperationException($"Unknown CompareOp: {op}"),
    };

    private static string WriteBoolConnective(BoolConnective op) => op switch
    {
        BoolConnective.And => "&&",
        BoolConnective.Or => "||",
        _ => throw new InvalidOperationException($"Unknown BoolConnective: {op}"),
    };

    private static string WriteArithOp(ArithOp op) => op switch
    {
        ArithOp.Add => "+",
        ArithOp.Sub => "-",
        ArithOp.Mul => "*",
        ArithOp.Div => "/",
        _ => throw new InvalidOperationException($"Unknown ArithOp: {op}"),
    };
}
