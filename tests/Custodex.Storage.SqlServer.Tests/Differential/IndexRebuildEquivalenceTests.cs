using CsCheck;

using Custodex.Abstractions;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>Property tests asserting that <c>ListObjects</c> results from an
/// <c>IndexedAuthorizer</c> backed by a fully rebuilt reverse index are set-equal to those from the
/// in-memory oracle, over randomly generated valid models. The conditioned variant runs under real
/// condition evaluation so a deep condition reached through unconditioned edges must be marked and
/// re-checked, not returned blind.</summary>
[Collection("sqlserver")]
public class IndexRebuildEquivalenceTests(SqlServerFixture fx)
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

    private async Task AssertEquivalentAsync(Gen<GeneratedModel> gen, string prefix, int iter)
    {
        var counter = 0;
        await Check.SampleAsync(gen, async model =>
        {
            var store = $"{prefix}-{Interlocked.Increment(ref counter)}";
            var (oracle, indexed) = await DifferentialHarness.BuildIndexedAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            foreach (var subject in model.ProbeSubjects)
            {
                var o = await DrainObjectsAsync(oracle, tenant, subject, obj.Type, perm);
                var i = await DrainObjectsAsync(indexed, tenant, subject, obj.Type, perm);
                if (!o.SetEquals(i)) return false;
            }
            return true;
        }, iter: iter, threads: 1);
    }

    /// <summary>Samples random conditioned models, performs a full reverse-index rebuild for each, and
    /// asserts that for every <c>(probeSubject, objectType, permission)</c> triple the index-backed and
    /// oracle <c>ListObjects</c> result sets are equal under real condition evaluation.</summary>
    [Fact]
    public Task Index_list_objects_set_equals_oracle_after_full_rebuild_under_conditions() =>
        AssertEquivalentAsync(ModelGenerator.ConditionedGen, "idx-rb-cond", iter: 60);

    /// <summary>Samples random unconditioned models, performs a full reverse-index rebuild for each, and
    /// asserts the index-backed and oracle <c>ListObjects</c> result sets are equal.</summary>
    [Fact]
    public Task Index_list_objects_set_equals_oracle_after_full_rebuild() =>
        AssertEquivalentAsync(ModelGenerator.Gen, "idx-rb", iter: 50);
}
