using Custodex.Abstractions;
using Custodex.Core.Evaluation;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

public sealed partial class SqlServerCteAuthorizer
{
    /// <summary>
    /// Evaluates every item against the active schema over a single connection, sharing one
    /// memo across the batch, and returns one result per item in request order.
    /// </summary>
    public async Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
    {
        var index = await LoadSchemaAsync(request.Tenant.Store, ct);
        return await RunAsync(async conn =>
        {
            var ctx = new EvalContext(_options);
            var results = new List<CheckResult>(request.Items.Count);
            foreach (var item in request.Items)
            {
                var outcome = await CheckPermissionAsync(
                    conn, index, request.Tenant, item.Object, item.Permission, item.Subject,
                    request.Context, ctx, explain: null, ct);
                results.Add(outcome.ToCheckResult());
            }
            return (IReadOnlyList<CheckResult>)results;
        }, ct);
    }
}
