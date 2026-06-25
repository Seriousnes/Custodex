using Custodex.Abstractions;

namespace Custodex.Core;

/// <summary>
/// Fluent builder for a permission expression. Each term added widens the grant by union (<c>+</c>); the
/// combining operators (<see cref="Intersect"/>, <see cref="Exclude"/>, <see cref="Conditioned"/>) fold the
/// expression built so far against a new operand.
/// </summary>
public sealed class PermExprBuilder
{
    private PermExpr? _current;

    private PermExprBuilder Add(PermExpr node)
    {
        _current = _current is null ? node : new Union(_current, node);
        return this;
    }

    /// <summary>Adds the holders of a relation on the current type as a term of the expression.</summary>
    /// <param name="relation">The relation whose holders are granted.</param>
    /// <returns>This builder, for chaining.</returns>
    public PermExprBuilder Relation(string relation) => Add(new RelationRef(relation));

    /// <summary>Adds an arrow term: follow <paramref name="relation"/> from the current object, granting when the subject holds <paramref name="permission"/> on an object the relation reaches.</summary>
    /// <param name="relation">The relation on the current object to follow.</param>
    /// <param name="permission">The permission required on the object the relation reaches.</param>
    /// <returns>This builder, for chaining.</returns>
    public PermExprBuilder Arrow(string relation, string permission) => Add(new Arrow(relation, permission));

    /// <summary>Adds a grouped sub-expression as a union term, so the grant widens when the subject satisfies it.</summary>
    /// <param name="build">Callback that builds the grouped sub-expression.</param>
    /// <returns>This builder, for chaining.</returns>
    public PermExprBuilder Union(Action<PermExprBuilder> build) => Add(BuildSub(build));

    /// <summary>Intersects (<c>&amp;</c>) the expression built so far with a sub-expression: the grant holds only when both are satisfied.</summary>
    /// <param name="build">Callback that builds the right-hand sub-expression.</param>
    /// <returns>This builder, for chaining.</returns>
    public PermExprBuilder Intersect(Action<PermExprBuilder> build)
    {
        _current = new Intersect(Require(), BuildSub(build));
        return this;
    }

    /// <summary>Excludes (<c>-</c>) a sub-expression from the expression built so far: the grant holds only when the sub-expression is not satisfied.</summary>
    /// <param name="build">Callback that builds the sub-expression to subtract.</param>
    /// <returns>This builder, for chaining.</returns>
    public PermExprBuilder Exclude(Action<PermExprBuilder> build)
    {
        _current = new Exclude(Require(), BuildSub(build));
        return this;
    }

    /// <summary>Gates the expression built so far on a named condition: the grant holds only when that condition also passes.</summary>
    /// <param name="conditionName">The name of the condition that must pass.</param>
    /// <returns>This builder, for chaining.</returns>
    public PermExprBuilder Conditioned(string conditionName)
    {
        _current = new Conditioned(Require(), conditionName);
        return this;
    }

    /// <summary>Produces the assembled permission expression.</summary>
    /// <returns>The expression AST.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no terms have been added.</exception>
    public PermExpr Build() => Require();

    private PermExpr Require() => _current ?? throw new InvalidOperationException("Permission expression has no terms.");

    private static PermExpr BuildSub(Action<PermExprBuilder> build)
    {
        var sub = new PermExprBuilder();
        build(sub);
        return sub.Build();
    }
}
