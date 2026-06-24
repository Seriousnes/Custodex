using CsCheck;
using Custodex.Abstractions;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Property tests asserting set equivalence of <c>ListObjects</c> and <c>ListSubjects</c>
/// between <c>NpgsqlCteAuthorizer</c> and <c>EngineDrivenAuthorizer</c> over randomly generated
/// valid models. Result sets are drained across all pages before comparison so pagination order
/// differences do not mask membership divergences.</summary>
[Collection("postgres")]
public class ListEquivalenceTests(PostgresFixture fx) : IAsyncLifetime
{
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Drains all pages of <c>ListObjects</c> from <paramref name="auth"/> and returns
    /// the complete set of object ids.</summary>
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

    /// <summary>Drains all pages of <c>ListSubjects</c> from <paramref name="auth"/> and returns
    /// the complete set of subjects serialized via <see cref="SubjectRef.ToString"/>.</summary>
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

    /// <summary>Returns the names of all permissions defined on <paramref name="type"/>
    /// in the generated model's schema.</summary>
    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);

    /// <summary>Samples random valid models and asserts that for every
    /// <c>(probeSubject, objectType, permission)</c> triple the CTE and oracle
    /// <c>ListObjects</c> result sets are equal.</summary>
    [Fact]
    public async Task Cte_list_objects_set_equals_oracle_over_random_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.Gen, async model =>
        {
            var store = $"lo-{Interlocked.Increment(ref counter)}";
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
        }, iter: 150);
    }

    /// <summary>Samples random valid models and asserts that for every
    /// <c>(probeObject, permission)</c> pair the CTE and oracle <c>ListSubjects</c>
    /// result sets are equal.</summary>
    [Fact]
    public async Task Cte_list_subjects_set_equals_oracle_over_random_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.Gen, async model =>
        {
            var store = $"ls-{Interlocked.Increment(ref counter)}";
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
        }, iter: 150);
    }
}
