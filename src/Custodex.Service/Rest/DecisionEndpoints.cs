using Custodex.Abstractions;

namespace Custodex.Service.Rest;

public static partial class RestEndpoints
{
    static partial void MapDecisionEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/check", async (CheckRequestDto req, IAuthorizer auth, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var result = await auth.CheckAsync(new CheckRequest(
                RestMap.Tenant(req.Store, req.Tenant),
                RestMap.FromDto(req.Object),
                req.Permission,
                ctx.Subject,
                ctx,
                req.Explain), ct);

            return Results.Ok(new CheckResponseDto(
                result.Allowed,
                result.Explain is not null ? RestMap.ToDto(result.Explain) : null));
        })
        .WithName("Check")
        .WithSummary("Evaluate a single authorization check.");

        group.MapPost("/batch-check", async (BatchCheckRequestDto req, IAuthorizer auth, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var tenant = RestMap.Tenant(req.Store, req.Tenant);
            var items = req.Checks.Select(c =>
                new CheckItem(RestMap.FromDto(c.Object), c.Permission, ctx.Subject)).ToList();

            var results = await auth.BatchCheckAsync(new BatchCheckRequest(tenant, items, ctx), ct);

            return Results.Ok(new BatchCheckResponseDto(
                results.Select(r => new CheckResponseDto(
                    r.Allowed,
                    r.Explain is not null ? RestMap.ToDto(r.Explain) : null)).ToList()));
        })
        .WithName("BatchCheck")
        .WithSummary("Evaluate multiple authorization checks sharing one request context.");

        group.MapPost("/list-objects", async (ListObjectsRequestDto req, IAuthorizer auth, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var result = await auth.ListObjectsAsync(new ListObjectsRequest(
                RestMap.Tenant(req.Store, req.Tenant),
                ctx.Subject,
                req.ObjectType,
                req.Permission,
                ctx,
                req.PageSize > 0 ? req.PageSize : 50,
                req.ContinuationToken), ct);

            return Results.Ok(new ListObjectsResponseDto(result.ObjectIds, result.ContinuationToken));
        })
        .WithName("ListObjects")
        .WithSummary("List objects of a given type that a subject may access via a permission.");

        group.MapPost("/list-subjects", async (ListSubjectsRequestDto req, IAuthorizer auth, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
                RestMap.Tenant(req.Store, req.Tenant),
                RestMap.FromDto(req.Object),
                req.Permission,
                ctx,
                req.PageSize > 0 ? req.PageSize : 50,
                req.ContinuationToken), ct);

            return Results.Ok(new ListSubjectsResponseDto(
                result.Subjects.Select(RestMap.ToDto).ToList(),
                result.ContinuationToken));
        })
        .WithName("ListSubjects")
        .WithSummary("List subjects who may access an object via a permission.");
    }
}
