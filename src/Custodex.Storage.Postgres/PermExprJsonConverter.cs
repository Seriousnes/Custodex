using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Polymorphic (de)serialization for the abstract <see cref="PermExpr"/> and
/// <see cref="ConditionExpr"/> ASTs via a <c>$type</c> discriminator. Needed because the contract's
/// records carry no System.Text.Json polymorphism attributes.
/// </summary>
public sealed class PermExprJsonConverter : JsonConverterFactory
{
    private static readonly IReadOnlyDictionary<string, Type> PermTypes = new Dictionary<string, Type>
    {
        ["relation"] = typeof(RelationRef),
        ["union"] = typeof(Union),
        ["intersect"] = typeof(Intersect),
        ["exclude"] = typeof(Exclude),
        ["arrow"] = typeof(Arrow),
        ["conditioned"] = typeof(Conditioned),
    };

    private static readonly IReadOnlyDictionary<string, Type> ConditionTypes = new Dictionary<string, Type>
    {
        ["empty"] = typeof(EmptyConditionBody),
        ["literalBool"] = typeof(LiteralBool),
        ["literalInt"] = typeof(LiteralInt),
        ["literalDouble"] = typeof(LiteralDouble),
        ["literalString"] = typeof(LiteralString),
        ["paramRef"] = typeof(ParamRef),
        ["attributeRef"] = typeof(AttributeRef),
        ["contextNow"] = typeof(ContextNow),
        ["contextSubject"] = typeof(ContextSubject),
        ["compare"] = typeof(Compare),
        ["boolOp"] = typeof(BoolOp),
        ["not"] = typeof(Not),
        ["arithmetic"] = typeof(Arithmetic),
        ["inList"] = typeof(InList),
        ["hourOf"] = typeof(HourOf),
    };

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(PermExpr) || typeToConvert == typeof(ConditionExpr);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(PermExpr))
            return new DiscriminatedConverter<PermExpr>(PermTypes);
        return new DiscriminatedConverter<ConditionExpr>(ConditionTypes);
    }

    private sealed class DiscriminatedConverter<TBase>(IReadOnlyDictionary<string, Type> byTag)
        : JsonConverter<TBase> where TBase : class
    {
        private readonly Dictionary<Type, string> _byType =
            byTag.ToDictionary(kv => kv.Value, kv => kv.Key);

        public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader)?.AsObject()
                ?? throw new JsonException("Expected a JSON object for a discriminated AST node.");
            var tag = node["$type"]?.GetValue<string>()
                ?? throw new JsonException("Discriminated AST node is missing '$type'.");
            if (!byTag.TryGetValue(tag, out var concrete))
                throw new JsonException($"Unknown {typeof(TBase).Name} '$type' '{tag}'.");
            node.Remove("$type");
            return (TBase?)node.Deserialize(concrete, options);
        }

        public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
        {
            var concrete = value.GetType();
            if (!_byType.TryGetValue(concrete, out var tag))
                throw new JsonException($"Cannot serialize {typeof(TBase).Name} of runtime type {concrete.Name}.");
            var node = JsonSerializer.SerializeToNode(value, concrete, options)!.AsObject();
            node["$type"] = tag;
            node.WriteTo(writer, options);
        }
    }
}
