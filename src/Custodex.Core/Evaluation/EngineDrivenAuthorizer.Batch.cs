using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        var ctx = new EvalContext(_options);   // one shared memo across all items
        var results = new List<CheckResult>(request.Items.Count);

        foreach (var item in request.Items)
        {
            var allowed = await CheckPermissionAsync(
                index, request.Tenant, item.Object, item.Permission, item.Subject,
                request.Context, ctx, explain: null, ct);
            results.Add(new CheckResult(allowed));
        }
        return results;
    }
}
