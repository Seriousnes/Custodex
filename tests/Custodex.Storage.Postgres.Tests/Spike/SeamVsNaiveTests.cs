using Dapper;

using Npgsql;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Spike;

[Collection("postgres")]
public class SeamVsNaiveTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        await SpikeSeed.LoadAsync(conn, SpikeData.CaseA);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string NaiveSql = """
        WITH editors AS (
            SELECT e.subject_type, e.subject_id
            FROM relation_tuples a
            JOIN relation_tuples e
              ON e.store_id = a.store_id AND e.tenant_id = a.tenant_id
             AND e.object_type = a.subject_type AND e.object_id = a.subject_id
             AND e.relation = 'editor'
            WHERE a.store_id = @store AND a.tenant_id = @tenant
              AND a.object_type = 'doc' AND a.object_id = @oid AND a.relation = 'folder'
        ),
        blocked_on_doc AS (
            SELECT subject_type, subject_id FROM relation_tuples
            WHERE store_id = @store AND tenant_id = @tenant
              AND object_type = 'doc' AND object_id = @oid AND relation = 'blocked'
        )
        SELECT EXISTS (
            SELECT 1 FROM editors x
            WHERE x.subject_type = 'user' AND x.subject_id = @sid
              AND NOT EXISTS (SELECT 1 FROM blocked_on_doc b
                              WHERE b.subject_type = x.subject_type AND b.subject_id = x.subject_id)
        )
        """;

    private static async Task<bool> NaiveAllowsAsync(NpgsqlConnection conn, string oid, string sid) =>
        await conn.ExecuteScalarAsync<bool>(NaiveSql,
            new { store = SpikeData.Store, tenant = SpikeData.Tenant, oid, sid });

    private static async Task<bool> SeamAllowsAsync(NpgsqlConnection conn, string docId, string sid)
    {
        var folders = await ReachabilityCte.SubjectsThroughRelationAsync(
            conn, SpikeData.Store, SpikeData.Tenant, "doc", docId, "folder");

        foreach (var (etype, eid) in folders)
        {
            var editors = await ReachabilityCte.SubjectsThroughRelationAsync(
                conn, SpikeData.Store, SpikeData.Tenant, etype, eid, "editor");
            var blocked = await ReachabilityCte.SubjectsThroughRelationAsync(
                conn, SpikeData.Store, SpikeData.Tenant, etype, eid, "blocked");

            var isEditor = editors.Contains(("user", sid));
            var isBlocked = blocked.Contains(("user", sid));
            if (isEditor && !isBlocked) return true;
        }
        return false;
    }

    [Fact]
    public async Task Naive_all_in_sql_returns_the_WRONG_answer_for_the_inner_exclusion()
    {
        await using var conn = await fx.OpenAsync();
        (await NaiveAllowsAsync(conn, "D1", "carol")).ShouldBeTrue();
    }

    [Fact]
    public async Task Decided_seam_matches_the_hand_computed_truth_for_case_A()
    {
        await using var conn = await fx.OpenAsync();
        (await SeamAllowsAsync(conn, "D1", "carol"))
            .ShouldBe(SpikeData.CaseA_Expected[("D1", "carol")]);
        (await SeamAllowsAsync(conn, "D1", "dana"))
            .ShouldBe(SpikeData.CaseA_Expected[("D1", "dana")]);
    }
}
