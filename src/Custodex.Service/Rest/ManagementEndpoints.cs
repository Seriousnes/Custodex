using Custodex.Abstractions;
using Custodex.Service.Mapping;
using Custodex.Service.Tenancy;

using Microsoft.AspNetCore.Mvc;

namespace Custodex.Service.Rest;

public static partial class RestEndpoints
{
    static partial void MapManagementEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/tuples", async (
            WriteTuplesRequestDto req, IRelationManager relations, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            var tuples = req.Tuples.Select(RestMap.FromDto).ToList();
            var token = await relations.WriteTuplesAsync(tc.Current, req.Actor, tuples, ct);
            return Results.Ok(new WriteResultDto(token.Value));
        })
        .WithName("WriteTuples")
        .WithSummary("Write relation tuples (audited).");

        group.MapDelete("/tuples", async (
            [FromBody] DeleteTuplesRequestDto req, IRelationManager relations, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            var tuples = req.Tuples.Select(RestMap.FromDto).ToList();
            var token = await relations.DeleteTuplesAsync(tc.Current, req.Actor, tuples, ct);
            return Results.Ok(new WriteResultDto(token.Value));
        })
        .WithName("DeleteTuples")
        .WithSummary("Delete relation tuples (audited).");

        group.MapPut("/attributes", async (
            WriteAttributesRequestDto req, IRelationManager relations, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            var token = await relations.WriteAttributesAsync(
                tc.Current,
                req.Actor,
                RestMap.FromDto(req.Object),
                req.Attributes,
                ct);
            return Results.Ok(new WriteResultDto(token.Value));
        })
        .WithName("WriteAttributes")
        .WithSummary("Write resource attributes used in condition evaluation.");

        group.MapPost("/tuples/query", async (
            ReadTuplesRequestDto req, IRelationManager relations, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            var filter = new TupleFilter(
                req.ObjectType, req.ObjectId, req.Relation, req.SubjectType, req.SubjectId);
            var tuples = await relations.ReadTuplesAsync(tc.Current, filter, ct);
            return Results.Ok(new ReadTuplesResponseDto([.. tuples.Select(RestMap.ToDto)]));
        })
        .WithName("ReadTuples")
        .WithSummary("Read relation tuples by filter.");

        group.MapPost("/change-log/query", async (
            ReadChangeLogRequestDto req, IRelationManager relations, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            var filter = new ChangeLogFilter(req.Since, req.Actor, req.Limit);
            var entries = await relations.ReadChangeLogAsync(tc.Current, filter, ct);
            var dtos = entries.Select(e => new ChangeLogEntryDto(
                e.Id, e.Actor, e.Operation, e.Target, e.Before, e.After, e.OccurredAt)).ToList();
            return Results.Ok(new ReadChangeLogResponseDto(dtos));
        })
        .WithName("ReadChangeLog")
        .WithSummary("Read the audited change log.");

        group.MapPost("/schema/validate", (ValidateSchemaRequestDto req, ISchemaManager schemas) =>
        {
            var schema = SchemaJson.Deserialize(req.SchemaJson);
            if (schema is null)
                return Results.Ok(new ValidateSchemaResponseDto(false, ["Schema JSON is null or empty."]));
            var result = schemas.ValidateSchema(schema);
            return Results.Ok(new ValidateSchemaResponseDto(result.IsValid, result.Errors));
        })
        .WithName("ValidateSchema")
        .WithSummary("Validate a schema without activating it.");

        group.MapPut("/schema/{store}", async (
            string store, SetActiveSchemaRequestDto req, ISchemaManager schemas, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            if (!string.Equals(store, tc.AuthenticatedStore, StringComparison.Ordinal))
                return Results.Forbid();

            var schema = SchemaJson.Deserialize(req.SchemaJson);
            if (schema is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["schemaJson"] = ["Schema JSON is null or empty."],
                });

            try
            {
                await schemas.SetActiveSchemaAsync(store, schema, ct);
                return Results.Ok();
            }
            catch (SchemaValidationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["schema"] = [.. ex.Errors],
                });
            }
        })
        .WithName("SetActiveSchema")
        .WithSummary("Validate and activate a schema for a store.");

        group.MapGet("/schema/{store}", async (
            string store, ISchemaManager schemas, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            if (!string.Equals(store, tc.AuthenticatedStore, StringComparison.Ordinal))
                return Results.Forbid();

            var schema = await schemas.GetActiveSchemaAsync(store, ct);
            if (schema is null)
                return Results.Ok(new GetActiveSchemaResponseDto(false, null));
            return Results.Ok(new GetActiveSchemaResponseDto(true, SchemaJson.Serialize(schema)));
        })
        .WithName("GetActiveSchema")
        .WithSummary("Get the active schema for a store.");

        group.MapPost("/stores", async (
            CreateStoreRequestDto req, IStoreManager stores, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            if (!string.Equals(req.Store, tc.AuthenticatedStore, StringComparison.Ordinal))
                return Results.Forbid();

            await stores.CreateStoreAsync(req.Store, ct);
            return Results.Created($"/api/schema/{req.Store}", null);
        })
        .WithName("CreateStore")
        .WithSummary("Provision a new store.");

        group.MapPost("/tenants", async (
            CreateTenantRequestDto req, ITenantManager tenants, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            if (!string.Equals(req.Store, tc.AuthenticatedStore, StringComparison.Ordinal))
                return Results.Forbid();

            await tenants.CreateTenantAsync(new TenantContext(req.Store, req.Tenant), ct);
            return Results.Created();
        })
        .WithName("CreateTenant")
        .WithSummary("Provision a new tenant within a store.");
    }
}
