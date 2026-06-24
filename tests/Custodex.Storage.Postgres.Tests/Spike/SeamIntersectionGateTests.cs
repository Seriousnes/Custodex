using Npgsql;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Spike;

[Collection("postgres")]
public class SeamIntersectionGateTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await SpikeSeed.LoadAsync(conn, SpikeData.CaseB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<bool> HoldsRelationAsync(
        NpgsqlConnection conn, string objType, string objId, string relation, string sid)
    {
        var leaves = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, objType, objId, relation);
        return leaves.Contains(("user", sid)) || leaves.Contains(("user", "*"));
    }

    private static async Task<bool> SeamAccessAsync(NpgsqlConnection conn, string animalId, string sid)
    {
        var enclosures = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "animal", animalId, "enclosure");
        var quarantine = false;
        foreach (var (etype, eid) in enclosures)
            if (await HoldsRelationAsync(conn, etype, eid, "is_quarantine", sid)) { quarantine = true; break; }

        var vet = await HoldsRelationAsync(conn, "animal", animalId, "vet_member", sid);
        var trained = await HoldsRelationAsync(conn, "animal", animalId, "trained_member", sid);
        return quarantine && vet && trained;
    }

    [Theory]
    [InlineData("dr-smith", true)]
    [InlineData("jones", false)]
    [InlineData("outsider", false)]
    public async Task Seam_matches_the_quarantine_intersection_truth(string sid, bool expected)
    {
        await using var conn = await fx.OpenAsync();
        (await SeamAccessAsync(conn, "EL-001", sid))
            .ShouldBe(SpikeData.CaseB_Expected[("EL-001", sid)]);
        (await SeamAccessAsync(conn, "EL-001", sid)).ShouldBe(expected);
    }
}
