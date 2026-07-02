using Custodex.Abstractions;

namespace Custodex.Core;

/// <summary>
/// Fluent builder for assembling a <see cref="Schema"/>: declare entity types with their relations and
/// permissions, and the named conditions permissions may be gated on, then call <see cref="Build"/>.
/// </summary>
/// <remarks>Creates a builder for a schema tagged with the given <paramref name="version"/> identifier.</remarks>
/// <param name="version">The version label recorded on the produced <see cref="Schema"/>, used to distinguish revisions of a permission model.</param>
public sealed partial class SchemaBuilder(string version)
{
    private readonly string _version = version;
    private readonly List<EntityTypeDef> _types = [];
    private readonly List<ConditionDef> _conditions = [];

    /// <summary>Declares an entity type and configures its relations and permissions through <paramref name="build"/>.</summary>
    /// <param name="name">The entity type name (for example the type of an object that permissions are checked on).</param>
    /// <param name="build">Callback that adds the type's relations and permissions.</param>
    /// <returns>This builder, for chaining.</returns>
    public SchemaBuilder Type(string name, Action<EntityTypeBuilder> build)
    {
        var b = new EntityTypeBuilder();
        build(b);
        var (relations, permissions) = b.Build();
        _types.Add(new EntityTypeDef(name, relations, permissions));
        return this;
    }

    /// <summary>Declares a named condition with typed parameters but no body, leaving its predicate to be supplied elsewhere.</summary>
    /// <param name="name">The condition name, referenced when a permission is gated on it.</param>
    /// <param name="build">Callback that declares the condition's typed parameters.</param>
    /// <returns>This builder, for chaining.</returns>
    public SchemaBuilder Condition(string name, Action<ConditionParamBuilder> build)
    {
        var b = new ConditionParamBuilder();
        build(b);
        _conditions.Add(new ConditionDef(name, b.Build(), new EmptyConditionBody()));
        return this;
    }

    /// <summary>Declares a named condition with typed parameters and a predicate body that decides whether it passes.</summary>
    /// <param name="name">The condition name, referenced when a permission is gated on it.</param>
    /// <param name="params">Callback that declares the condition's typed parameters.</param>
    /// <param name="body">Callback that builds the boolean predicate evaluated against the parameters, request attributes, and context.</param>
    /// <returns>This builder, for chaining.</returns>
    public SchemaBuilder Condition(
        string name, Action<ConditionParamBuilder> @params, Func<ConditionBodyBuilder, ConditionExpr> body)
    {
        var paramBuilder = new ConditionParamBuilder();
        @params(paramBuilder);
        var bodyExpr = body(new ConditionBodyBuilder());
        _conditions.Add(new ConditionDef(name, paramBuilder.Build(), bodyExpr));
        return this;
    }

    /// <summary>Produces the immutable <see cref="Schema"/> from the declared types and conditions.</summary>
    /// <returns>The assembled schema AST.</returns>
    public Schema Build() => new(_version, _types, _conditions);
}
