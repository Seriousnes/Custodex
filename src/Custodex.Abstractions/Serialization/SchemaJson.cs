using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custodex.Abstractions.Serialization;

/// <summary>
/// Serializes and deserializes a <see cref="Schema"/> AST to and from its canonical JSON representation. The
/// polymorphic <see cref="PermExpr"/> and <see cref="ConditionExpr"/> nodes carry a <c>$kind</c> discriminator
/// declared on the node types themselves, so the sealed AST round-trips with no custom converter. The
/// discriminator is accepted in any position, so JSON written by earlier revisions still reads back.
/// </summary>
public static class SchemaJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>Serializes a <see cref="Schema"/> to its canonical JSON representation.</summary>
    public static string Serialize(Schema schema) => JsonSerializer.Serialize(schema, Options);

    /// <summary>
    /// Deserializes a <see cref="Schema"/> from its canonical JSON representation. Returns
    /// <see langword="null"/> for a <see langword="null"/> input and throws
    /// <see cref="JsonException"/> when the input is not well-formed JSON.
    /// </summary>
    public static Schema? Deserialize(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Schema>(json, Options);
}
