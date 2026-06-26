using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>End-to-end smoke test that verifies the dual-seed harness seeds identical models
/// into the in-memory oracle and the Postgres CTE path, and that both authorizers agree on
/// the hand-picked S2 discriminator case.</summary>
[Collection("postgres")]
public class HarnessSmokeTests(PostgresFixture fx) : IAsyncLifetime
{
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Seeds the S2 discriminator model into both authorizers and asserts they agree:
    /// carol (both editor and blocked on crate e1) is denied <c>asset.edit</c> by both the
    /// in-memory oracle and the Postgres CTE path.</summary>
    [Fact]
    public async Task Both_authorizers_agree_on_discriminator_model_and_deny_carol()
    {
        var model = new GeneratedModel(
            ModelGeneratorSamples.S2DiscriminatorSchema(),
            ModelGeneratorSamples.S2DiscriminatorTuples(),
            [],
            [new EntityRef("asset", "a1")],
            [new SubjectRef("user", "carol"), new SubjectRef("user", "dana")]);

        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store: "smoke");

        var tenant = new TenantContext("smoke", "t");
        var carolCtx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "carol"), new Dictionary<string, object?>());
        var carolReq = new CheckRequest(tenant, new EntityRef("asset", "a1"), "edit", new SubjectRef("user", "carol"), carolCtx);

        var oracleCarol = await oracle.CheckAsync(carolReq);
        var cteCarol = await cte.CheckAsync(carolReq);

        oracleCarol.Allowed.ShouldBe(cteCarol.Allowed);
        oracleCarol.Allowed.ShouldBeFalse();
    }
}
