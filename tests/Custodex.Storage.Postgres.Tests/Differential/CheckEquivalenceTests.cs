using CsCheck;
using Custodex.Abstractions;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Property test asserting that <c>NpgsqlCteAuthorizer</c> and <c>EngineDrivenAuthorizer</c>
/// return identical <see cref="CheckResult.Allowed"/> values over randomly generated valid models.
/// A divergence indicates a bug in the CTE path — the in-memory oracle is the spec.</summary>
[Collection("postgres")]
public class CheckEquivalenceTests(PostgresFixture fx) : IAsyncLifetime
{
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Samples random valid models and asserts that for every
    /// <c>(probeObject, permission, probeSubject)</c> triple the CTE authorizer's
    /// <see cref="CheckResult.Allowed"/> matches the oracle's.</summary>
    [Fact]
    public async Task Cte_check_equals_oracle_check_over_random_models()
    {
        var counter = 0;
        await Check.SampleAsync(ModelGenerator.Gen, async model =>
        {
            var store = $"chk-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            foreach (var subject in model.ProbeSubjects)
            {
                var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());
                var req = new CheckRequest(tenant, obj, perm, subject, ctx);
                var o = (await oracle.CheckAsync(req)).Allowed;
                var c = (await cte.CheckAsync(req)).Allowed;
                if (o != c) return false;
            }
            return true;
        }, iter: 200);
    }

    /// <summary>Returns the names of all permissions defined on <paramref name="type"/>
    /// in the generated model's schema.</summary>
    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);
}
