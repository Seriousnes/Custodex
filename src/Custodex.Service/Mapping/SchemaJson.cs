using Custodex.Abstractions;
using CoreJson = Custodex.Core.Serialization.SchemaJson;

namespace Custodex.Service.Mapping;

/// <summary>
/// Forwards schema serialization to the canonical <c>Custodex.Core.Serialization.SchemaJson</c>
/// so the service and all clients share one <c>$kind</c> discriminator set.
/// </summary>
public static class SchemaJson
{
    /// <summary>Serializes a <see cref="Schema"/> to its canonical JSON representation.</summary>
    public static string Serialize(Schema schema) => CoreJson.Serialize(schema);

    /// <summary>Deserializes a <see cref="Schema"/> from its canonical JSON representation.</summary>
    public static Schema? Deserialize(string json) => CoreJson.Deserialize(json);
}
