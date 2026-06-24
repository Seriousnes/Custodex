using CsCheck;
using Custodex.Abstractions;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Property tests asserting that <c>ListObjects</c> results from an
/// <c>IndexedAuthorizer</c> backed by a fully rebuilt reverse index are set-equal to those
/// from the in-memory oracle, over randomly generated valid models.</summary>
[Collection("postgres")]
public class IndexRebuildEquivalenceTests(PostgresFixture fx) : IAsyncLifetime
{
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

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

    /// <summary>Samples random valid models, performs a full reverse-index rebuild for each, and
    /// asserts that for every <c>(probeSubject, objectType, permission)</c> triple the index-backed
    /// and oracle <c>ListObjects</c> result sets are equal.</summary>
    [Fact]
    public async Task Index_list_objects_set_equals_oracle_after_full_rebuild()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.Gen, async model =>
        {
            var store = $"idx-rebuild-{Interlocked.Increment(ref counter)}";
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
        }, iter: 50);
    }
}
