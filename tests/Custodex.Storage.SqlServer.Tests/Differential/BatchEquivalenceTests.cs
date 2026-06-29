using CsCheck;

using Custodex.Abstractions;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>Property test asserting that <c>SqlServerCteAuthorizer.BatchCheck</c> returns, item for
/// item, the same allow/deny decisions as the <c>EngineDrivenAuthorizer</c> oracle over a batch
/// covering every probe triple of a randomly generated model.</summary>
[Collection("sqlserver")]
public class BatchEquivalenceTests(SqlServerFixture fx)
{
    /// <summary>Builds one batch of every <c>(probeObject, permission, probeSubject)</c> triple and
    /// asserts the CTE batch result matches the oracle batch result position by position.</summary>
    [Fact]
    public async Task Cte_batch_check_equals_oracle_batch_over_random_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.Gen, async model =>
        {
            var store = $"bc-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            var items = new List<CheckItem>();
            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            foreach (var subject in model.ProbeSubjects)
                items.Add(new CheckItem(obj, perm, subject));

            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>());
            var req = new BatchCheckRequest(tenant, items, ctx);

            var o = await oracle.BatchCheckAsync(req);
            var c = await cte.BatchCheckAsync(req);

            if (o.Count != c.Count) return false;
            for (var i = 0; i < o.Count; i++)
                if (o[i].Allowed != c[i].Allowed) return false;
            return true;
        }, iter: 40, threads: 1);
    }

    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);
}
