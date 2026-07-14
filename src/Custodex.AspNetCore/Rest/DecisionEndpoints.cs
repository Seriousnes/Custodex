using Custodex.Abstractions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

public static partial class RestEndpoints
{
    static partial void MapDecisionEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/check", async (CheckRequestDto req, IAuthorizer auth, ITenantContextAccessor tc, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var result = await auth.CheckAsync(new CheckRequest(
                tc.Current,
                RestMap.FromDto(req.Object),
                req.Permission,
                ctx.Subject,
                ctx,
                req.Explain), ct);

            return Results.Ok(RestMap.ToDto(result));
        })
        .WithName("Check")
        .WithSummary("Evaluate a single authorization check.");

        group.MapPost("/batch-check", async (BatchCheckRequestDto req, IAuthorizer auth, ITenantContextAccessor tc, IOptions<BatchCheckOptions> batchOptions, CancellationToken ct) =>
        {
            var maxItems = batchOptions.Value.MaxItems;
            if (req.Checks.Count > maxItems)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["checks"] = [$"Batch exceeds the maximum of {maxItems} items."],
                });

            var ctx = RestMap.FromDto(req.Context);
            var items = req.Checks.Select(c =>
                new CheckItem(RestMap.FromDto(c.Object), c.Permission, ctx.Subject)).ToList();

            var results = await auth.BatchCheckAsync(new BatchCheckRequest(tc.Current, items, ctx), ct);

            return Results.Ok(new BatchCheckResponseDto([.. results.Select(RestMap.ToDto)]));
        })
        .WithName("BatchCheck")
        .WithSummary("Evaluate multiple authorization checks sharing one request context.");

        group.MapPost("/list-objects", async (ListObjectsRequestDto req, IAuthorizer auth, ITenantContextAccessor tc, IOptions<PageSizeOptions> pageOptions, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var result = await auth.ListObjectsAsync(new ListObjectsRequest(
                tc.Current,
                ctx.Subject,
                req.ObjectType,
                req.Permission,
                ctx,
                ClampPageSize(req.PageSize, 50, pageOptions.Value.Max),
                req.ContinuationToken), ct);

            return Results.Ok(new ListObjectsResponseDto(result.ObjectIds, result.ContinuationToken));
        })
        .WithName("ListObjects")
        .WithSummary("List objects of a given type that a subject may access via a permission.");

        group.MapPost("/list-subjects", async (ListSubjectsRequestDto req, IAuthorizer auth, ITenantContextAccessor tc, IOptions<PageSizeOptions> pageOptions, CancellationToken ct) =>
        {
            var ctx = RestMap.FromDto(req.Context);
            var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
                tc.Current,
                RestMap.FromDto(req.Object),
                req.Permission,
                ctx,
                ClampPageSize(req.PageSize, 50, pageOptions.Value.Max),
                req.ContinuationToken), ct);

            return Results.Ok(new ListSubjectsResponseDto(
                [.. result.Subjects.Select(RestMap.ToDto)],
                result.ContinuationToken));
        })
        .WithName("ListSubjects")
        .WithSummary("List subjects who may access an object via a permission.");
    }

    private static int ClampPageSize(int requested, int fallback, int max) =>
        Math.Min(requested > 0 ? requested : fallback, max);
}
