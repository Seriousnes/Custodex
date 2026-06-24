using CsCheck;
using Custodex.Abstractions;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Property tests asserting that an incrementally maintained reverse index produces
/// <c>ListObjects</c> results set-equal to the in-memory oracle and to a fresh full rebuild,
/// over randomly generated valid models with add/remove write sequences.</summary>
[Collection("postgres")]
public class IndexIncrementalEquivalenceTests(PostgresFixture fx) : IAsyncLifetime
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

    /// <summary>Samples random valid models, applies their tuples as an incremental write sequence,
    /// and asserts that for every <c>(probeSubject, objectType, permission)</c> triple the
    /// incrementally maintained index and the oracle <c>ListObjects</c> result sets are equal.
    /// Then performs a full rebuild of the same tenant and asserts the same equality again.</summary>
    [Fact]
    public async Task Incremental_index_equals_oracle_and_rebuild_after_write_sequences()
    {
        var counter = 0;
        var gen = ModelGenerator.Gen.Select(m =>
            (Model: m, Ops: (IReadOnlyList<WriteOp>)IndexModelGenerators.BuildOps(m.Tuples)));

        await Check.SampleAsync(gen, async pair =>
        {
            var store = $"idx-incr-{Interlocked.Increment(ref counter)}";
            var (oracle, indexed) = await DifferentialHarness.BuildIncrementalAsync(fx, pair.Model.Schema, pair.Ops, store);
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
        }, iter: 50);
    }
}
