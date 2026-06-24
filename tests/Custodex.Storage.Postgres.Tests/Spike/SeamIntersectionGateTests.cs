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

    private static async Task<bool> SeamAccessAsync(NpgsqlConnection conn, string docId, string sid)
    {
        var folders = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "doc", docId, "folder");
        var flagged = false;
        foreach (var (etype, eid) in folders)
            if (await HoldsRelationAsync(conn, etype, eid, "is_quarantine", sid)) { flagged = true; break; }

        var vetMbr = await HoldsRelationAsync(conn, "doc", docId, "vet_member", sid);
        var trained = await HoldsRelationAsync(conn, "doc", docId, "trained_member", sid);
        return flagged && vetMbr && trained;
    }

    [Theory]
    [InlineData("pat", true)]
    [InlineData("jones", false)]
    [InlineData("outsider", false)]
    public async Task Seam_matches_the_quarantine_intersection_truth(string sid, bool expected)
    {
        await using var conn = await fx.OpenAsync();
        (await SeamAccessAsync(conn, "D1", sid))
            .ShouldBe(SpikeData.CaseB_Expected[("D1", sid)]);
        (await SeamAccessAsync(conn, "D1", sid)).ShouldBe(expected);
    }
}
