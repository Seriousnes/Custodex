using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core.Serialization;

/// <summary>
/// Serializes and deserializes a <see cref="Schema"/> AST to/from a canonical JSON string.
/// Uses a <c>$kind</c> polymorphic discriminator for <see cref="PermExpr"/> and
/// <see cref="ConditionExpr"/> subtypes so the sealed AST round-trips over the wire.
/// </summary>
public static class SchemaJson
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new SchemaAstConverter());
        return opts;
    }

    /// <summary>Serializes a <see cref="Schema"/> to its canonical JSON representation.</summary>
    public static string Serialize(Schema schema) =>
        JsonSerializer.Serialize(schema, Options);

    /// <summary>Deserializes a <see cref="Schema"/> from its canonical JSON representation.</summary>
    public static Schema? Deserialize(string json) =>
        JsonSerializer.Deserialize<Schema>(json, Options);

    private sealed class SchemaAstConverter : JsonConverterFactory
    {
        private static readonly IReadOnlyDictionary<string, Type> PermKinds = new Dictionary<string, Type>
        {
            ["relation"] = typeof(RelationRef),
            ["union"] = typeof(Union),
            ["intersect"] = typeof(Intersect),
            ["exclude"] = typeof(Exclude),
            ["arrow"] = typeof(Arrow),
            ["conditioned"] = typeof(Conditioned),
        };

        private static readonly IReadOnlyDictionary<string, Type> CondKinds = new Dictionary<string, Type>
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

        public override bool CanConvert(Type t) =>
            t == typeof(PermExpr) || t == typeof(ConditionExpr);

        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions opts) =>
            t == typeof(PermExpr)
                ? new KindConverter<PermExpr>(PermKinds)
                : new KindConverter<ConditionExpr>(CondKinds);
    }

    private sealed class KindConverter<TBase>(IReadOnlyDictionary<string, Type> byKind)
        : JsonConverter<TBase> where TBase : class
    {
        private readonly Dictionary<Type, string> _byType =
            byKind.ToDictionary(kv => kv.Value, kv => kv.Key);

        public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions opts)
        {
            var node = JsonNode.Parse(ref reader)?.AsObject()
                ?? throw new JsonException("Expected JSON object for AST node.");
            var kind = node["$kind"]?.GetValue<string>()
                ?? throw new JsonException("AST node missing '$kind'.");
            if (!byKind.TryGetValue(kind, out var concrete))
                throw new JsonException($"Unknown {typeof(TBase).Name} '$kind' '{kind}'.");
            node.Remove("$kind");
            return (TBase?)node.Deserialize(concrete, opts);
        }

        public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions opts)
        {
            var concrete = value.GetType();
            if (!_byType.TryGetValue(concrete, out var kind))
                throw new JsonException($"Cannot serialize {typeof(TBase).Name} of type {concrete.Name}.");
            var node = JsonSerializer.SerializeToNode(value, concrete, opts)!.AsObject();
            node["$kind"] = kind;
            node.WriteTo(writer, opts);
        }
    }
}
