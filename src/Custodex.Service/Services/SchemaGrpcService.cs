using Custodex.Service.Mapping;
using Custodex.V1;
using Grpc.Core;
using Contracts = Custodex.Abstractions;

namespace Custodex.Service.Services;

/// <summary>
/// gRPC service for schema validation and activation, delegating to <see cref="Contracts.ISchemaManager"/>.
/// </summary>
public sealed class SchemaGrpcService(Contracts.ISchemaManager schemas) : Schema.SchemaBase
{
    /// <inheritdoc />
    public override Task<ValidateSchemaResponse> Validate(ValidateSchemaRequest request, ServerCallContext context)
    {
        var schema = SchemaJson.Deserialize(request.SchemaJson);
        if (schema is null)
        {
            var resp = new ValidateSchemaResponse { IsValid = false };
            resp.Errors.Add("Schema JSON is null or empty.");
            return Task.FromResult(resp);
        }
        Contracts.SchemaValidationResult result = schemas.ValidateSchema(schema);
        var response = new ValidateSchemaResponse { IsValid = result.IsValid };
        response.Errors.AddRange(result.Errors);
        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public override async Task<SetActiveSchemaResponse> SetActive(SetActiveSchemaRequest request, ServerCallContext context)
    {
        var schema = SchemaJson.Deserialize(request.SchemaJson);
        if (schema is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Schema JSON is null or empty."));

        try
        {
            await schemas.SetActiveSchemaAsync(request.Store, schema, context.CancellationToken);
        }
        catch (Contracts.SchemaValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, string.Join("; ", ex.Errors)));
        }

        return new SetActiveSchemaResponse();
    }

    /// <inheritdoc />
    public override async Task<GetActiveSchemaResponse> GetActive(GetActiveSchemaRequest request, ServerCallContext context)
    {
        var schema = await schemas.GetActiveSchemaAsync(request.Store, context.CancellationToken);
        if (schema is null)
            return new GetActiveSchemaResponse { Found = false };
        return new GetActiveSchemaResponse
        {
            Found = true,
            SchemaJson = SchemaJson.Serialize(schema),
        };
    }
}
