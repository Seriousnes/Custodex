using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Materializes JSON values typed as <see langword="object"/> into concrete CLR primitives rather
/// than leaving them as <see cref="JsonElement"/>, so attribute and condition-parameter bags round-trip
/// to the same runtime types a consumer stored: JSON numbers become <see cref="long"/> or
/// <see cref="double"/>, booleans become <see cref="bool"/>, strings become <see cref="string"/>,
/// arrays become <see cref="List{T}"/> of <see langword="object"/>, and objects become
/// <see cref="Dictionary{TKey, TValue}"/> keyed by <see cref="string"/>.
/// </summary>
public sealed class JsonObjectConverter : JsonConverter<object?>
{
    /// <inheritdoc />
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader, options),
            JsonTokenType.StartObject => ReadObject(ref reader, options),
            _ => throw new JsonException($"Unexpected token '{reader.TokenType}' while reading an object value."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var runtime = value.GetType();
        if (runtime == typeof(object))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        JsonSerializer.Serialize(writer, value, runtime, options);
    }

    private static object ReadNumber(ref Utf8JsonReader reader) =>
        reader.TryGetInt64(out var l) ? (object)l : reader.GetDouble();

    private static List<object?> ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var items = new List<object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            items.Add(JsonSerializer.Deserialize<object?>(ref reader, options));
        return items;
    }

    private static Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            map[name] = JsonSerializer.Deserialize<object?>(ref reader, options);
        }
        return map;
    }
}
