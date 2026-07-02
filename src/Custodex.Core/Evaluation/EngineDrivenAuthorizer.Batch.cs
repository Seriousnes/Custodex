using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        var ctx = new EvalContext(_options);
        var results = new List<CheckResult>(request.Items.Count);

        foreach (var item in request.Items)
        {
            var outcome = await CheckPermissionAsync(
                index, request.Tenant, item.Object, item.Permission, item.Subject,
                request.Context, ctx, explain: null, ct);
            results.Add(outcome.ToCheckResult());
        }
        return results;
    }
}
