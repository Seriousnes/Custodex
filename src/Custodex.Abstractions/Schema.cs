using System.Text.Json.Serialization;

namespace Custodex.Abstractions;

/// <summary>
/// A complete, versioned permission model for one store: the entity types with their relations and
/// permissions, plus the named conditions those permissions may reference. This is the developer-authored
/// artifact the engine evaluates against; it carries no tenant data.
/// </summary>
/// <param name="Version">An opaque label identifying this schema revision.</param>
/// <param name="Types">The entity types the model declares.</param>
/// <param name="Conditions">The named conditions available to permission expressions.</param>
public sealed record Schema(string Version, IReadOnlyList<EntityTypeDef> Types, IReadOnlyList<ConditionDef> Conditions);

/// <summary>One entity type and the relations and permissions declared on it.</summary>
/// <param name="Name">The type name, referenced by tuples and queries.</param>
/// <param name="Relations">The relations objects of this type can have to subjects.</param>
/// <param name="Permissions">The permissions computed from those relations.</param>
public sealed record EntityTypeDef(string Name, IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions);

/// <summary>
/// A relation an object may have to subjects, and the subject shapes a tuple on it may name. Relations
/// are the stored edges of the graph; permissions are computed over them.
/// </summary>
/// <param name="Name">The relation name.</param>
/// <param name="AllowedSubjects">The subject shapes — type, subject set, or wildcard — permitted as the subject of a tuple on this relation.</param>
public sealed record RelationDef(string Name, IReadOnlyList<SubjectTypeRef> AllowedSubjects);

/// <summary>
/// A subject shape a relation accepts: a subject type, optionally narrowed to subject sets of a given
/// relation, or opened to the type-wide wildcard.
/// </summary>
/// <param name="Type">The permitted subject entity type.</param>
/// <param name="Relation">When set, only subject sets <c>type:id#relation</c> of this relation are permitted.</param>
/// <param name="Wildcard">When <see langword="true"/>, the wildcard subject <c>type:*</c> is permitted, granting to every subject of the type.</param>
public sealed record SubjectTypeRef(string Type, string? Relation = null, bool Wildcard = false);

/// <summary>A named permission and the expression that computes who holds it.</summary>
/// <param name="Name">The permission name, queried by Check and the list operations.</param>
/// <param name="Expression">The expression evaluated to decide the permission.</param>
public sealed record PermissionDef(string Name, PermExpr Expression);

/// <summary>
/// The base of the permission-expression algebra. A permission is computed by composing relations with
/// union, intersection, exclusion, relation traversal (arrow), and conditions. The node set is closed:
/// the engine switches over it exhaustively.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(RelationRef), "relation")]
[JsonDerivedType(typeof(Union), "union")]
[JsonDerivedType(typeof(Intersect), "intersect")]
[JsonDerivedType(typeof(Exclude), "exclude")]
[JsonDerivedType(typeof(Arrow), "arrow")]
[JsonDerivedType(typeof(Conditioned), "conditioned")]
public abstract record PermExpr;

/// <summary>
/// Draws holders from a relation: a subject satisfies this when it holds <paramref name="Relation"/> on
/// the object directly, via the type-wide wildcard, or as a member of a subject set the relation grants
/// to (resolved recursively).
/// </summary>
/// <param name="Relation">The relation on the current type to draw holders from.</param>
public sealed record RelationRef(string Relation) : PermExpr;

/// <summary>Union (<c>+</c>): satisfied when the subject satisfies <paramref name="Left"/> or <paramref name="Right"/>.</summary>
/// <param name="Left">The first operand.</param>
/// <param name="Right">The second operand.</param>
public sealed record Union(PermExpr Left, PermExpr Right) : PermExpr;

/// <summary>Intersection (<c>&amp;</c>): satisfied only when the subject satisfies both <paramref name="Left"/> and <paramref name="Right"/>.</summary>
/// <param name="Left">The first operand.</param>
/// <param name="Right">The second operand.</param>
public sealed record Intersect(PermExpr Left, PermExpr Right) : PermExpr;

/// <summary>Exclusion (<c>-</c>): satisfied when the subject satisfies <paramref name="Left"/> but not <paramref name="Right"/>.</summary>
/// <param name="Left">The operand granting access.</param>
/// <param name="Right">The operand removing access.</param>
public sealed record Exclude(PermExpr Left, PermExpr Right) : PermExpr;

/// <summary>
/// Traversal (<c>relation-&gt;permission</c>): follow <paramref name="Relation"/> to each related object,
/// then require <paramref name="Permission"/> there (its full permission expression when the target type
/// defines one, otherwise that relation directly). This is how permissions are inherited across the
/// object graph — for example a folder's viewer becoming a document's viewer.
/// </summary>
/// <param name="Relation">The relation on the current object to follow.</param>
/// <param name="Permission">The permission required on the object the relation reaches.</param>
public sealed record Arrow(string Relation, string Permission) : PermExpr;

/// <summary>Gates an expression with a condition (ABAC): <paramref name="Inner"/> grants only when the named condition also passes.</summary>
/// <param name="Inner">The expression whose grant is gated.</param>
/// <param name="ConditionName">The name of the condition that must pass.</param>
public sealed record Conditioned(PermExpr Inner, string ConditionName) : PermExpr;

/// <summary>A named, parameterised predicate (ABAC) that permissions and tuples can reference.</summary>
/// <param name="Name">The condition name, referenced by <see cref="Conditioned"/> and <see cref="ConditionRef"/>.</param>
/// <param name="Parameters">The typed parameters the body reads.</param>
/// <param name="Body">The boolean expression evaluated against the parameters and the request attributes.</param>
public sealed record ConditionDef(string Name, IReadOnlyList<ConditionParam> Parameters, ConditionExpr Body);

/// <summary>One typed parameter of a <see cref="ConditionDef"/>.</summary>
/// <param name="Name">The parameter name, as bound by tuples and read by the body.</param>
/// <param name="Type">The parameter's value type.</param>
public sealed record ConditionParam(string Name, ConditionType Type);

/// <summary>The value types a <see cref="ConditionParam"/> may take.</summary>
public enum ConditionType
{
    /// <summary>A boolean value.</summary>
    Bool,

    /// <summary>A 32-bit signed integer.</summary>
    Int,

    /// <summary>A 64-bit signed integer.</summary>
    Long,

    /// <summary>A double-precision floating-point number.</summary>
    Double,

    /// <summary>A string value.</summary>
    String,

    /// <summary>An instant in time.</summary>
    Timestamp
}

/// <summary>The outcome of validating a <see cref="Schema"/> before it is activated.</summary>
/// <param name="IsValid">Whether the schema passed validation.</param>
/// <param name="Errors">The validation error messages; empty when valid.</param>
public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);
