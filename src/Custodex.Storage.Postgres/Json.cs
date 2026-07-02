using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Centralised JSON helper for reading and writing <c>jsonb</c> columns.
/// All stores use this to (de)serialize condition parameters, attribute dictionaries,
/// schema definitions, and change-log payloads.
/// </summary>
public static class Json
{
    internal static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PermExprJsonConverter());
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

    /// <summary>
    /// Deserializes a JSON object into a string-keyed map whose values are materialized as CLR
    /// primitives (<see langword="bool"/>, <see langword="long"/>, <see langword="double"/>,
    /// <see langword="string"/>, or <see langword="null"/>) rather than <see cref="JsonElement"/>.
    /// Use this for attribute bags and condition parameters so the condition evaluator sees the same
    /// runtime types the in-memory stores hold; values that are not JSON scalars are returned as their
    /// raw JSON text. Returns <see langword="null"/> when <paramref name="json"/> is <see langword="null"/>.
    /// </summary>
    public static Dictionary<string, object?>? DeserializeValues(string? json)
    {
        if (json is null) return null;
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options);
        if (raw is null) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, element) in raw)
            values[key] = ToClrValue(element);
        return values;
    }

    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };
}
