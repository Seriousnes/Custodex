using CsCheck;

using Custodex.Abstractions;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>Property tests asserting that an incrementally maintained reverse index produces
/// <c>ListObjects</c> results set-equal to the in-memory oracle and to a fresh full rebuild, over
/// randomly generated conditioned models with add/remove write sequences. Running under real
/// condition evaluation exercises the deep-condition-via-unconditioned-edge marking through the
/// incremental maintainer.</summary>
[Collection("sqlserver")]
public class IndexIncrementalEquivalenceTests(SqlServerFixture fx)
{
    private static async Task<HashSet<string>> DrainObjectsAsync(
        IAuthorizer auth, TenantContext tenant, SubjectRef subject, string type, string perm)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());
            var page = await auth.ListObjectsAsync(
                new ListObjectsRequest(tenant, subject, type, perm, ctx, PageSize: 2, ContinuationToken: token));
            foreach (var id in page.ObjectIds) ids.Add(id);
            token = page.ContinuationToken;
        } while (token is not null);
        return ids;
    }

    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);

    /// <summary>Samples random conditioned models, applies their tuples as an incremental write
    /// sequence, and asserts that for every <c>(probeSubject, objectType, permission)</c> triple the
    /// incrementally maintained index and the oracle <c>ListObjects</c> result sets are equal under real
    /// condition evaluation. Then performs a full rebuild of the same tenant and asserts the same
    /// equality again.</summary>
    [Fact]
    public async Task Incremental_index_equals_oracle_and_rebuild_under_conditions()
    {
        var counter = 0;
        var gen = ModelGenerator.ConditionedGen.Select(m =>
            (Model: m, Ops: (IReadOnlyList<WriteOp>)IndexModelGenerators.BuildOps(m.Tuples)));

        await Check.SampleAsync(gen, async pair =>
        {
            var store = $"idx-incr-cond-{Interlocked.Increment(ref counter)}";
            var (oracle, indexed) = await DifferentialHarness.BuildIncrementalIndexedAsync(fx, pair.Model, pair.Ops, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in pair.Model.ProbeObjects)
            foreach (var perm in PermissionsOf(pair.Model, obj.Type))
            foreach (var subject in pair.Model.ProbeSubjects)
            {
                var o = await DrainObjectsAsync(oracle, tenant, subject, obj.Type, perm);
                var i = await DrainObjectsAsync(indexed, tenant, subject, obj.Type, perm);
                if (!o.SetEquals(i)) return false;
            }

            await DifferentialHarness.RebuildInPlaceAsync(fx, store);

            foreach (var obj in pair.Model.ProbeObjects)
            foreach (var perm in PermissionsOf(pair.Model, obj.Type))
            foreach (var subject in pair.Model.ProbeSubjects)
            {
                var o = await DrainObjectsAsync(oracle, tenant, subject, obj.Type, perm);
                var r = await DrainObjectsAsync(indexed, tenant, subject, obj.Type, perm);
                if (!o.SetEquals(r)) return false;
            }
            return true;
        }, iter: 40, threads: 1);
    }
}
