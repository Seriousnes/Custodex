using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Custodex.Abstractions;

namespace Custodex.Storage.MySql;

/// <summary>
/// Centralised JSON helper for reading and writing the JSON-bearing text columns.
/// All stores use this to (de)serialize condition parameters, attribute dictionaries,
/// schema definitions, and change-log payloads. Scalar JSON values are materialized to native
/// CLR types (<see cref="bool"/>, <see cref="long"/>, <see cref="double"/>, <see cref="string"/>),
/// so a value stored as an integer round-trips as a <see cref="long"/> and a condition or attribute
/// evaluates against its declared type.
/// </summary>
public static class Json
{
    internal static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PermExprJsonConverter());
        options.Converters.Add(new ObjectJsonConverter());
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SuppressAstPolymorphism } };
        return options;
    }

    private static void SuppressAstPolymorphism(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(PermExpr) || typeInfo.Type == typeof(ConditionExpr))
            typeInfo.PolymorphismOptions = null;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to a JSON string, preserving generic type information
    /// so that abstract base types (such as <see cref="Custodex.Abstractions.PermExpr"/>) are
    /// dispatched through the registered polymorphic converter.
    /// </summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Serializes a value whose static type is <see langword="object"/> to a JSON string using its
    /// runtime type. Use the generic overload when the abstract base type must control dispatch.
    /// </summary>
    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserializes a JSON string to <typeparamref name="T"/>.
    /// Returns <see langword="default"/> when <paramref name="json"/> is <see langword="null"/>.
    /// </summary>
    public static T? Deserialize<T>(string? json) =>
        json is null ? default : JsonSerializer.Deserialize<T>(json, Options);
}

internal sealed class ObjectJsonConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader, options),
            JsonTokenType.StartArray => ReadArray(ref reader, options),
            _ => throw new JsonException($"Unsupported JSON token '{reader.TokenType}'."),
        };

    private static object ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var l))
            return l;
        return reader.GetDouble();
    }

    private Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            dict[name] = Read(ref reader, typeof(object), options);
        }
        return dict;
    }

    private List<object?> ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var list = new List<object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            list.Add(Read(ref reader, typeof(object), options));
        return list;
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var runtimeType = value.GetType();
        if (runtimeType == typeof(object))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        JsonSerializer.Serialize(writer, value, runtimeType, options);
    }
}
