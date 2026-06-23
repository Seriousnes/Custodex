namespace Relkit.Abstractions;

public sealed record Schema(string Version, IReadOnlyList<EntityTypeDef> Types, IReadOnlyList<ConditionDef> Conditions);

public sealed record EntityTypeDef(string Name, IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions);

public sealed record RelationDef(string Name, IReadOnlyList<SubjectTypeRef> AllowedSubjects);
public sealed record SubjectTypeRef(string Type, string? Relation = null, bool Wildcard = false);

public sealed record PermissionDef(string Name, PermExpr Expression);

public abstract record PermExpr;
public sealed record RelationRef(string Relation) : PermExpr;
public sealed record Union(PermExpr Left, PermExpr Right) : PermExpr;
public sealed record Intersect(PermExpr Left, PermExpr Right) : PermExpr;
public sealed record Exclude(PermExpr Left, PermExpr Right) : PermExpr;
public sealed record Arrow(string Relation, string Permission) : PermExpr;
public sealed record Conditioned(PermExpr Inner, string ConditionName) : PermExpr;

public sealed record ConditionDef(string Name, IReadOnlyList<ConditionParam> Parameters, ConditionExpr Body);
public sealed record ConditionParam(string Name, ConditionType Type);
public enum ConditionType { Bool, Int, Long, Double, String, Timestamp }

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);
