using Custodex.Abstractions;

using AbstractionsJson = Custodex.Abstractions.Serialization.SchemaJson;

namespace Custodex.Client.Serialization;

/// <summary>
/// Forwards schema serialization to the canonical <c>Custodex.Abstractions.Serialization.SchemaJson</c>
/// so the client and service share one <c>$kind</c> discriminator set.
/// </summary>
public static class SchemaJson
{
    /// <summary>Serializes a <see cref="Schema"/> to its canonical JSON representation.</summary>
    public static string Serialize(Schema schema) => AbstractionsJson.Serialize(schema);

    /// <summary>Deserializes a <see cref="Schema"/> from its canonical JSON representation.</summary>
    public static Schema? Deserialize(string json) => AbstractionsJson.Deserialize(json);
}
