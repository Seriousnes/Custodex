using Custodex.Abstractions;

using Dapper;

using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Dapper-backed <see cref="IIndexStore"/> over the reverse_index and index_build_markers tables.
/// Mechanical CRUD only — the expansion deciding which rows exist lives in the rebuild and
/// incremental-maintenance paths. Writes enlist in the supplied unit of work; reads open their own
/// connection. Every statement hard-filters (store_id, tenant_id) and, where row-scoped, schema_version.
/// </summary>
public sealed class NpgsqlIndexStore(string connectionString) : IIndexStore
{
    private readonly string _cs = CustodexSchema.Apply(connectionString);

    private const string UpsertSql = """
        INSERT INTO custodex.reverse_index
            (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
        VALUES (@store, @tenant, @sv, @subject, @permission, @ot, @oid, @conditioned)
        ON CONFLICT (store_id, tenant_id, schema_version, subject, permission, object_type, object_id)
        DO UPDATE SET conditioned = EXCLUDED.conditioned
        """;

    /// <inheritdoc />
    public async Task UpsertAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var w = NpgsqlUnitOfWork.From(uow);
        foreach (var r in rows)
        {
            await w.Connection.ExecuteAsync(new CommandDefinition(UpsertSql, new
            {
                store = t.Store, tenant = t.Tenant, sv = schemaVersion,
                subject = r.Subject, permission = r.Permission, ot = r.ObjectType, oid = r.ObjectId,
                conditioned = r.Conditioned
            }, transaction: w.Transaction, cancellationToken: ct));
        }
    }

    /// <inheritdoc />
    public async Task DeleteForObjectAsync(TenantContext t, string schemaVersion, string objectType, string objectId,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM custodex.reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND object_type = @ot AND object_id = @oid
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion, ot = objectType, oid = objectId },
            transaction: w.Transaction, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task DeleteRowsAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var w = NpgsqlUnitOfWork.From(uow);
        const string sql = """
            DELETE FROM custodex.reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND subject = @subject AND permission = @permission
              AND object_type = @ot AND object_id = @oid
            """;
        foreach (var r in rows)
        {
            await w.Connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                store = t.Store, tenant = t.Tenant, sv = schemaVersion,
                subject = r.Subject, permission = r.Permission, ot = r.ObjectType, oid = r.ObjectId
            }, transaction: w.Transaction, cancellationToken: ct));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReverseIndexRow>> QueryObjectsAsync(TenantContext t, string schemaVersion,
        string subject, string permission, string objectType, int limit, string? afterObjectId,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT subject AS Subject, permission AS Permission, object_type AS ObjectType,
                   object_id AS ObjectId, conditioned AS Conditioned
            FROM custodex.reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND subject = @subject AND permission = @permission AND object_type = @ot
              AND (@after IS NULL OR object_id > @after)
            ORDER BY object_id
            LIMIT @limit
            """, new
            {
                store = t.Store, tenant = t.Tenant, sv = schemaVersion,
                subject, permission, ot = objectType, after = afterObjectId, limit
            }, cancellationToken: ct));
        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReverseIndexRow>> ReadForObjectAsync(TenantContext t, string schemaVersion,
        string objectType, string objectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT subject AS Subject, permission AS Permission, object_type AS ObjectType,
                   object_id AS ObjectId, conditioned AS Conditioned
            FROM custodex.reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND object_type = @ot AND object_id = @oid
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion, ot = objectType, oid = objectId },
            cancellationToken: ct));
        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task ClearAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM custodex.reverse_index WHERE store_id = @store AND tenant_id = @tenant",
            new { store = t.Store, tenant = t.Tenant }, transaction: w.Transaction, cancellationToken: ct));
        await w.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM custodex.index_build_markers WHERE store_id = @store AND tenant_id = @tenant",
            new { store = t.Store, tenant = t.Tenant }, transaction: w.Transaction, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<bool> IsBuiltAsync(TenantContext t, string schemaVersion, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM custodex.index_build_markers
                WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv)
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task MarkBuiltAsync(TenantContext t, string schemaVersion, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = NpgsqlUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO custodex.index_build_markers (store_id, tenant_id, schema_version)
            VALUES (@store, @tenant, @sv)
            ON CONFLICT (store_id, tenant_id, schema_version) DO UPDATE SET built_at = now()
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion },
            transaction: w.Transaction, cancellationToken: ct));
    }

    private static ReverseIndexRow Map(Row r) => new(r.Subject, r.Permission, r.ObjectType, r.ObjectId, r.Conditioned);

    private sealed record Row(string Subject, string Permission, string ObjectType, string ObjectId, bool Conditioned);
}
