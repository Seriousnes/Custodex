using Relkit.Abstractions;

namespace Relkit.Core;

/// <summary>Empty condition body placeholder; m0/06 replaces this with the real body AST.</summary>
public sealed record EmptyConditionBody : ConditionExpr;

public sealed partial class SchemaBuilder
{
    private readonly string _version;
    private readonly List<EntityTypeDef> _types = [];
    private readonly List<ConditionDef> _conditions = [];

    public SchemaBuilder(string version) => _version = version;

    public SchemaBuilder Type(string name, Action<EntityTypeBuilder> build)
    {
        var b = new EntityTypeBuilder();
        build(b);
        var (relations, permissions) = b.Build();
        _types.Add(new EntityTypeDef(name, relations, permissions));
        return this;
    }

    public SchemaBuilder Condition(string name, Action<ConditionParamBuilder> build)
    {
        var b = new ConditionParamBuilder();
        build(b);
        _conditions.Add(new ConditionDef(name, b.Build(), new EmptyConditionBody()));
        return this;
    }

    public SchemaBuilder Condition(
        string name, Action<ConditionParamBuilder> @params, Func<ConditionBodyBuilder, ConditionExpr> body)
    {
        var paramBuilder = new ConditionParamBuilder();
        @params(paramBuilder);
        var bodyExpr = body(new ConditionBodyBuilder());
        _conditions.Add(new ConditionDef(name, paramBuilder.Build(), bodyExpr));
        return this;
    }

    public Schema Build() => new(_version, _types, _conditions);
}
