using Relkit.Abstractions;

namespace Relkit.Core.Validation;

public static class SchemaValidator
{
    public static SchemaValidationResult Validate(Schema schema)
    {
        var errors = new List<string>();
        var types = IndexTypes(schema, errors);
        var conditions = IndexConditions(schema, errors);

        foreach (var cond in schema.Conditions)
            if (cond.Body is not EmptyConditionBody)
                errors.AddRange(ConditionBodyChecker.Check(cond));

        foreach (var type in schema.Types)
        {
            foreach (var perm in type.Permissions)
                ValidateExpr(type, perm.Name, perm.Expression, types, conditions, errors);
        }

        // Only run cycle detection when resolution succeeded; otherwise an unresolved
        // name would be mistaken for a cycle.
        if (errors.Count == 0)
            DetectCycles(schema, types, errors);

        return new SchemaValidationResult(errors.Count == 0, errors);
    }

    private static Dictionary<string, EntityTypeDef> IndexTypes(Schema schema, List<string> errors)
    {
        var types = new Dictionary<string, EntityTypeDef>(StringComparer.Ordinal);
        foreach (var type in schema.Types)
        {
            if (!types.TryAdd(type.Name, type))
                errors.Add($"Duplicate entity type '{type.Name}'.");

            var relNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in type.Relations)
                if (!relNames.Add(rel.Name))
                    errors.Add($"Type '{type.Name}' declares duplicate relation '{rel.Name}'.");

            var permNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var perm in type.Permissions)
                if (!permNames.Add(perm.Name))
                    errors.Add($"Type '{type.Name}' declares duplicate permission '{perm.Name}'.");
        }
        return types;
    }

    private static Dictionary<string, ConditionDef> IndexConditions(Schema schema, List<string> errors)
    {
        var conditions = new Dictionary<string, ConditionDef>(StringComparer.Ordinal);
        foreach (var cond in schema.Conditions)
            if (!conditions.TryAdd(cond.Name, cond))
                errors.Add($"Duplicate condition '{cond.Name}'.");
        return conditions;
    }

    private static bool HasRelation(EntityTypeDef type, string name) =>
        type.Relations.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    private static bool HasPermission(EntityTypeDef type, string name) =>
        type.Permissions.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static void ValidateExpr(
        EntityTypeDef type, string permission, PermExpr expr,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        IReadOnlyDictionary<string, ConditionDef> conditions,
        List<string> errors)
    {
        switch (expr)
        {
            case RelationRef r:
                if (!HasRelation(type, r.Relation) && !HasPermission(type, r.Relation))
                    errors.Add($"Permission '{type.Name}.{permission}' references '{r.Relation}', " +
                               $"which is neither a relation nor a permission on '{type.Name}'.");
                break;

            case Union u:
                ValidateExpr(type, permission, u.Left, types, conditions, errors);
                ValidateExpr(type, permission, u.Right, types, conditions, errors);
                break;

            case Intersect i:
                ValidateExpr(type, permission, i.Left, types, conditions, errors);
                ValidateExpr(type, permission, i.Right, types, conditions, errors);
                break;

            case Exclude e:
                ValidateExpr(type, permission, e.Left, types, conditions, errors);
                ValidateExpr(type, permission, e.Right, types, conditions, errors);
                break;

            case Arrow a:
                ValidateArrow(type, permission, a, types, errors);
                break;

            case Conditioned c:
                ValidateExpr(type, permission, c.Inner, types, conditions, errors);
                if (!conditions.ContainsKey(c.ConditionName))
                    errors.Add($"Permission '{type.Name}.{permission}' references condition " +
                               $"'{c.ConditionName}', which is not declared.");
                break;
        }
    }

    private static void ValidateArrow(
        EntityTypeDef type, string permission, Arrow arrow,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        var relation = type.Relations.FirstOrDefault(r =>
            string.Equals(r.Name, arrow.Relation, StringComparison.Ordinal));
        if (relation is null)
        {
            errors.Add($"Permission '{type.Name}.{permission}' arrows through relation " +
                       $"'{arrow.Relation}', which is not declared on '{type.Name}'.");
            return;
        }

        foreach (var filler in relation.AllowedSubjects)
        {
            // Subject-set fillers (e.g. group#member) name a relation, not an arrow target.
            if (filler.Relation is not null)
                continue;

            if (!types.TryGetValue(filler.Type, out var target))
            {
                errors.Add($"Permission '{type.Name}.{permission}' arrows into type " +
                           $"'{filler.Type}', which is not declared.");
                continue;
            }

            if (!HasPermission(target, arrow.Permission))
                errors.Add($"Permission '{type.Name}.{permission}' arrows to " +
                           $"'{filler.Type}.{arrow.Permission}', which is not a permission on '{filler.Type}'.");
        }
    }

    private enum Mark { White, Grey, Black }

    private static void DetectCycles(
        Schema schema,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        var marks = new Dictionary<(string Type, string Perm), Mark>();
        foreach (var type in schema.Types)
            foreach (var perm in type.Permissions)
                marks[(type.Name, perm.Name)] = Mark.White;

        foreach (var node in marks.Keys.ToList())
            if (marks[node] == Mark.White)
                Visit(node, marks, types, errors);
    }

    private static bool Visit(
        (string Type, string Perm) node,
        Dictionary<(string Type, string Perm), Mark> marks,
        IReadOnlyDictionary<string, EntityTypeDef> types,
        List<string> errors)
    {
        marks[node] = Mark.Grey;
        foreach (var next in Edges(node, types))
        {
            if (!marks.TryGetValue(next, out var mark))
                continue;   // edge to a non-permission node; resolution already covered it
            if (mark == Mark.Grey)
            {
                errors.Add($"Permission '{node.Type}.{node.Perm}' is part of a " +
                           $"non-terminating permission cycle.");
                marks[node] = Mark.Black;
                return true;
            }
            if (mark == Mark.White && Visit(next, marks, types, errors))
            {
                marks[node] = Mark.Black;
                return true;
            }
        }
        marks[node] = Mark.Black;
        return false;
    }

    private static IEnumerable<(string Type, string Perm)> Edges(
        (string Type, string Perm) node,
        IReadOnlyDictionary<string, EntityTypeDef> types)
    {
        var type = types[node.Type];
        var perm = type.Permissions.First(p => string.Equals(p.Name, node.Perm, StringComparison.Ordinal));
        foreach (var edge in ExprEdges(type, perm.Expression, types))
            yield return edge;
    }

    private static IEnumerable<(string Type, string Perm)> ExprEdges(
        EntityTypeDef type, PermExpr expr,
        IReadOnlyDictionary<string, EntityTypeDef> types)
    {
        switch (expr)
        {
            case RelationRef r when !HasRelation(type, r.Relation) && HasPermission(type, r.Relation):
                yield return (type.Name, r.Relation);
                break;
            case RelationRef:
                break;   // names a relation (possibly shadowing a same-named permission); resolved as a relation, not a permission edge
            case Union u:
                foreach (var e in ExprEdges(type, u.Left, types)) yield return e;
                foreach (var e in ExprEdges(type, u.Right, types)) yield return e;
                break;
            case Intersect i:
                foreach (var e in ExprEdges(type, i.Left, types)) yield return e;
                foreach (var e in ExprEdges(type, i.Right, types)) yield return e;
                break;
            case Exclude x:
                foreach (var e in ExprEdges(type, x.Left, types)) yield return e;
                foreach (var e in ExprEdges(type, x.Right, types)) yield return e;
                break;
            case Conditioned c:
                foreach (var e in ExprEdges(type, c.Inner, types)) yield return e;
                break;
            case Arrow a:
                var relation = type.Relations.First(r =>
                    string.Equals(r.Name, a.Relation, StringComparison.Ordinal));
                foreach (var filler in relation.AllowedSubjects)
                {
                    if (filler.Relation is not null) continue;
                    if (types.TryGetValue(filler.Type, out var target) && HasPermission(target, a.Permission))
                        yield return (filler.Type, a.Permission);
                }
                break;
        }
    }
}
