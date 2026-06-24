using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Spike;

[Collection("postgres")]
public class ReachabilityCteTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await SpikeSeed.LoadAsync(conn, SpikeData.CaseB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reachability_expands_a_subject_set_to_its_leaf_users()
    {
        await using var conn = await fx.OpenAsync();
        var subjects = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "doc", "D1", "vet_member");

        subjects.ShouldContain(("user", "pat"));
        subjects.ShouldContain(("user", "jones"));
        subjects.ShouldNotContain(("group", "reds"));
    }

    [Fact]
    public async Task Reachability_returns_the_wildcard_leaf_for_a_universal_gate()
    {
        await using var conn = await fx.OpenAsync();
        var subjects = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "folder", "Q1", "is_quarantine");

        subjects.ShouldContain(("user", "*"));
    }
}
