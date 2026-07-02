using CsCheck;

using Custodex.Abstractions;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Differential property tests over the conditioned generator: random condition-bearing models
/// are seeded into the in-memory oracle and the Postgres CTE path, both under the real condition
/// evaluator with identical attributes, and their answers must match for Check and the list
/// operations.</summary>
[Collection("postgres")]
public class ConditionedGeneratorEquivalenceTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);

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

    private static async Task<HashSet<string>> DrainSubjectsAsync(
        IAuthorizer auth, TenantContext tenant, EntityRef obj, string perm)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>());
            var page = await auth.ListSubjectsAsync(
                new ListSubjectsRequest(tenant, obj, perm, ctx, PageSize: 2, ContinuationToken: token));
            foreach (var s in page.Subjects) subjects.Add(s.ToString());
            token = page.ContinuationToken;
        } while (token is not null);
        return subjects;
    }

    [Fact]
    public async Task Cte_check_equals_oracle_over_random_conditioned_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.ConditionedGen, async model =>
        {
            var store = $"cck-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            foreach (var subject in model.ProbeSubjects)
            {
                var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());
                var req = new CheckRequest(tenant, obj, perm, subject, ctx);
                var o = (await oracle.CheckAsync(req)).Decision;
                var c = (await cte.CheckAsync(req)).Decision;
                if (o != c) return false;
            }
            return true;
        }, iter: 100);
    }

    [Fact]
    public async Task Cte_list_objects_equals_oracle_over_random_conditioned_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.ConditionedGen, async model =>
        {
            var store = $"clo-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            foreach (var subject in model.ProbeSubjects)
            {
                var o = await DrainObjectsAsync(oracle, tenant, subject, obj.Type, perm);
                var c = await DrainObjectsAsync(cte, tenant, subject, obj.Type, perm);
                if (!o.SetEquals(c)) return false;
            }
            return true;
        }, iter: 60);
    }

    [Fact]
    public async Task Cte_list_subjects_equals_oracle_over_random_conditioned_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.ConditionedGen, async model =>
        {
            var store = $"cls-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            {
                var o = await DrainSubjectsAsync(oracle, tenant, obj, perm);
                var c = await DrainSubjectsAsync(cte, tenant, obj, perm);
                if (!o.SetEquals(c)) return false;
            }
            return true;
        }, iter: 60);
    }
}
