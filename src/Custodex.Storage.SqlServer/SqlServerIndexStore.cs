using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Dapper-backed <see cref="IIndexStore"/> over the <c>reverse_index</c> and <c>index_build_markers</c>
/// tables. Mechanical CRUD only — the expansion deciding which rows exist lives in the rebuild and
/// incremental-maintenance paths. Writes enlist in the supplied unit of work; reads open their own
/// connection. Every statement hard-filters <c>(store_id, tenant_id)</c> and, where row-scoped,
/// <c>schema_version</c>. Identifier columns collate <c>Latin1_General_100_BIN2</c> so equality and the
/// scan's <c>ORDER BY object_id</c> are byte-ordinal regardless of the server's default collation.
/// </summary>
public sealed class SqlServerIndexStore(string connectionString) : IIndexStore
{
    private readonly string _connectionString = connectionString;

    private const string UpsertSql = """
        UPDATE custodex.reverse_index SET conditioned = @conditioned
        WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
          AND subject = @subject AND permission = @permission
          AND object_type = @ot AND object_id = @oid;
        IF @@ROWCOUNT = 0
            INSERT INTO custodex.reverse_index
                (store_id, tenant_id, schema_version, subject, permission, object_type, object_id, conditioned)
            VALUES (@store, @tenant, @sv, @subject, @permission, @ot, @oid, @conditioned);
        """;

    /// <inheritdoc />
    public async Task UpsertAsync(TenantContext t, string schemaVersion, IReadOnlyList<ReverseIndexRow> rows,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var w = SqlServerUnitOfWork.From(uow);
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
        var w = SqlServerUnitOfWork.From(uow);
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
        var w = SqlServerUnitOfWork.From(uow);
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
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition("""
            SELECT TOP (@limit) subject AS Subject, permission AS Permission, object_type AS ObjectType,
                   object_id AS ObjectId, conditioned AS Conditioned
            FROM custodex.reverse_index
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv
              AND subject = @subject AND permission = @permission AND object_type = @ot
              AND scan_key = CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT_WS(NCHAR(31),
                    @store, @tenant, @sv, @subject, @permission, @ot)))
              AND (@after IS NULL OR object_id > @after)
            ORDER BY object_id
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
        await using var conn = new SqlConnection(_connectionString);
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
        var w = SqlServerUnitOfWork.From(uow);
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
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM custodex.index_build_markers
                WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv) THEN 1 ELSE 0 END AS bit)
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task MarkBuiltAsync(TenantContext t, string schemaVersion, IUnitOfWork uow, CancellationToken ct = default)
    {
        var w = SqlServerUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE custodex.index_build_markers SET built_at = SYSUTCDATETIME()
            WHERE store_id = @store AND tenant_id = @tenant AND schema_version = @sv;
            IF @@ROWCOUNT = 0
                INSERT INTO custodex.index_build_markers (store_id, tenant_id, schema_version)
                VALUES (@store, @tenant, @sv);
            """, new { store = t.Store, tenant = t.Tenant, sv = schemaVersion },
            transaction: w.Transaction, cancellationToken: ct));
    }

    private static ReverseIndexRow Map(Row r) => new(r.Subject, r.Permission, r.ObjectType, r.ObjectId, r.Conditioned);

    private sealed record Row(string Subject, string Permission, string ObjectType, string ObjectId, bool Conditioned);
}
