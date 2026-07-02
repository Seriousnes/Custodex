using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Custodex.Abstractions;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Centralised JSON helper for reading and writing the provider's <c>nvarchar(max)</c> JSON columns.
/// All stores use this to (de)serialize condition parameters, attribute dictionaries, schema
/// definitions, and change-log payloads, with object-typed values materialized to concrete CLR types.
/// </summary>
public static class Json
{
    internal static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonObjectConverter());
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
}
