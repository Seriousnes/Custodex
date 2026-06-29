using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>Index-equivalence tests over handcrafted conditioned-exclusion shapes. Each shape is run
/// through a full reverse-index rebuild and through incremental maintenance, and the index-backed
/// <c>ListObjects</c> must equal the in-memory oracle under real condition evaluation. The shapes
/// discriminate the coarse marking recompute from the over-denying baseline (a, c), and guard that a
/// conservatively over-included row re-checks to a denial rather than being returned blind (b).</summary>
[Collection("sqlserver")]
public class ConditionedExclusionIndexEquivalenceTests(SqlServerFixture fx)
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

    public static IEnumerable<object[]> Cases() =>
        ConditionedExclusionModels.All().Select(c => new object[] { c.Name });

    private static ExclusionCase Case(string name) =>
        ConditionedExclusionModels.All().Single(c => c.Name == name);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Rebuilt_index_list_objects_equals_oracle(string name)
    {
        var c = Case(name);
        var store = $"cex-rb-{name}";
        var (oracle, indexed) = await DifferentialHarness.BuildIndexedAsync(fx, c.Model, store);
        var tenant = new TenantContext(store, "t");

        foreach (var (subject, expected) in c.Expectations)
        {
            var o = await DrainObjectsAsync(oracle, tenant, subject, c.ObjectType, c.Permission);
            var i = await DrainObjectsAsync(indexed, tenant, subject, c.ObjectType, c.Permission);
            o.SetEquals(expected).ShouldBeTrue(
                $"oracle {subject} -> [{string.Join(",", o)}] expected [{string.Join(",", expected)}]");
            i.SetEquals(o).ShouldBeTrue(
                $"indexed {subject} -> [{string.Join(",", i)}] oracle [{string.Join(",", o)}]");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Incrementally_maintained_index_list_objects_equals_oracle(string name)
    {
        var c = Case(name);
        var store = $"cex-incr-{name}";
        var ops = ConditionedExclusionModels.AddOps(c.Model);
        var (oracle, indexed) = await DifferentialHarness.BuildIncrementalIndexedAsync(fx, c.Model, ops, store);
        var tenant = new TenantContext(store, "t");

        foreach (var (subject, expected) in c.Expectations)
        {
            var o = await DrainObjectsAsync(oracle, tenant, subject, c.ObjectType, c.Permission);
            var i = await DrainObjectsAsync(indexed, tenant, subject, c.ObjectType, c.Permission);
            o.SetEquals(expected).ShouldBeTrue(
                $"oracle {subject} -> [{string.Join(",", o)}] expected [{string.Join(",", expected)}]");
            i.SetEquals(o).ShouldBeTrue(
                $"indexed {subject} -> [{string.Join(",", i)}] oracle [{string.Join(",", o)}]");
        }
    }
}
