using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custodex.Storage.Sqlite;

internal sealed class ObjectJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l))
                    return l;
                return reader.GetDouble();
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartObject:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return map;
                    var name = reader.GetString()!;
                    reader.Read();
                    map[name] = Read(ref reader, typeof(object), options);
                }
                throw new JsonException("Unterminated JSON object.");
            case JsonTokenType.StartArray:
                var items = new List<object?>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                        return items;
                    items.Add(Read(ref reader, typeof(object), options));
                }
                throw new JsonException("Unterminated JSON array.");
            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}' while reading an object value.");
        }
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
