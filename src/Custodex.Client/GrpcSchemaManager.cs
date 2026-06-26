using Custodex.Abstractions;
using Custodex.Client.Serialization;
using Custodex.Client.Transport;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="ISchemaManager"/> by forwarding calls to a remote
/// <c>Custodex.Service</c> via the gRPC <c>Schema</c> service.
/// </summary>
public sealed class GrpcSchemaManager(Proto.Schema.SchemaClient client) : ISchemaManager
{
    /// <inheritdoc/>
    public SchemaValidationResult ValidateSchema(Schema schema)
    {
        var proto = new Proto.ValidateSchemaRequest { SchemaJson = SchemaJson.Serialize(schema) };
        var response = RemoteStatus.Unwrap(() =>
            client.Validate(proto));
        return new SchemaValidationResult(response.IsValid, [.. response.Errors]);
    }

    /// <inheritdoc/>
    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        var proto = new Proto.SetActiveSchemaRequest
        {
            Store = store,
            SchemaJson = SchemaJson.Serialize(schema),
        };
        await RemoteStatus.UnwrapAsync(() =>
            client.SetActiveAsync(proto, cancellationToken: ct).ResponseAsync);
    }

    /// <inheritdoc/>
    public async Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default)
    {
        var proto = new Proto.GetActiveSchemaRequest { Store = store };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.GetActiveAsync(proto, cancellationToken: ct).ResponseAsync);
        if (!response.Found || string.IsNullOrEmpty(response.SchemaJson))
            return null;
        return SchemaJson.Deserialize(response.SchemaJson);
    }
}
