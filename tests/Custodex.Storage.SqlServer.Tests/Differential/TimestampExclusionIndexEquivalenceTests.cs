using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Differential;

/// <summary>Index-equivalence tests over the timestamp-gated conditioned-exclusion shape. The shape is
/// run through a full reverse-index rebuild and through incremental maintenance, and the index-backed
/// <c>ListObjects</c> must equal the in-memory oracle under real condition evaluation. The deciding
/// object is granted only because its gate compares false chronologically while comparing true by
/// ordinal string order, so the indexed answer tracks the oracle only when the conservatively kept gate
/// row is re-checked and the typed-timestamp comparison resolves chronologically.</summary>
[Collection("sqlserver")]
public class TimestampExclusionIndexEquivalenceTests(SqlServerFixture fx)
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
        TimestampExclusionModels.All().Select(c => new object[] { c.Name });

    private static ExclusionCase Case(string name) =>
        TimestampExclusionModels.All().Single(c => c.Name == name);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Rebuilt_index_list_objects_equals_oracle(string name)
    {
        var c = Case(name);
        var store = $"tex-rb-{name}";
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
        var store = $"tex-incr-{name}";
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
