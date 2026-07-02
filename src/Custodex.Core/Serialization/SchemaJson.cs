using Custodex.Abstractions;

using AbstractionsJson = Custodex.Abstractions.Serialization.SchemaJson;

namespace Custodex.Core.Serialization;

/// <summary>
/// Serializes and deserializes a <see cref="Schema"/> AST to and from its canonical JSON representation,
/// forwarding to <see cref="Custodex.Abstractions.Serialization.SchemaJson"/> so the engine, the service, and
/// clients share one <c>$kind</c> discriminator set.
/// </summary>
public static class SchemaJson
{
    /// <summary>Serializes a <see cref="Schema"/> to its canonical JSON representation.</summary>
    public static string Serialize(Schema schema) => AbstractionsJson.Serialize(schema);

    /// <summary>
    /// Deserializes a <see cref="Schema"/> from its canonical JSON representation. Returns
    /// <see langword="null"/> for a <see langword="null"/> input.
    /// </summary>
    public static Schema? Deserialize(string? json) => AbstractionsJson.Deserialize(json);
}
