using Dapper;
using Npgsql;

namespace Custodex.Storage.Postgres.Tests.Spike;

internal static class SpikeSeed
{
    internal static async Task LoadAsync(NpgsqlConnection conn, IReadOnlyList<SpikeData.Fact> facts)
    {
        await conn.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = SpikeData.Store });
        await conn.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = SpikeData.Store, t = SpikeData.Tenant });

        foreach (var f in facts)
            await conn.ExecuteAsync("""
                INSERT INTO relation_tuples
                    (store_id, tenant_id, object_type, object_id, relation,
                     subject_type, subject_id, subject_relation)
                VALUES (@store, @tenant, @ot, @oid, @rel, @st, @sid, @srel)
                ON CONFLICT DO NOTHING
                """,
                new
                {
                    store = SpikeData.Store, tenant = SpikeData.Tenant,
                    ot = f.ObjectType, oid = f.ObjectId, rel = f.Relation,
                    st = f.SubjectType, sid = f.SubjectId, srel = f.SubjectRelation,
                });
    }
}
