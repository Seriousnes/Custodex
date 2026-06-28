using Custodex.Storage.Postgres;
using Custodex.Studio.Views;

using Npgsql;

using NpgsqlTypes;

namespace Custodex.Service.Views;

/// <summary>
/// Postgres-backed <see cref="IStudioViewStore"/> owned by the console. Opens a short-lived
/// connection per operation from the supplied connection string and ensures its backing table
/// exists on first use. Every query filters on both <c>store</c> and <c>tenant</c>. Timestamps are
/// taken from <see cref="StudioView.UpdatedAt"/>; the store never reads the wall clock.
/// </summary>
public sealed class PostgresStudioViewStore : IStudioViewStore
{
    private const string SelectColumns =
        "store, tenant, owner, kind, key, config::text, updated_at";

    private readonly string _cs;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private bool _ensured;

    /// <summary>Creates a store that reads and writes through the given Custodex connection string.</summary>
    /// <param name="connectionString">The Postgres connection string for the Custodex database.</param>
    public PostgresStudioViewStore(string connectionString) =>
        _cs = CustodexSchema.Apply(connectionString);

    /// <inheritdoc />
    public async Task SaveAsync(StudioView view, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO custodex.studio_view (store, tenant, owner, kind, key, config, updated_at)
            VALUES (@store, @tenant, @owner, @kind, @key, @config, @updated_at)
            ON CONFLICT (store, tenant, owner, kind, key)
            DO UPDATE SET config = EXCLUDED.config, updated_at = EXCLUDED.updated_at
            """, conn);
        cmd.Parameters.AddWithValue("store", view.Store);
        cmd.Parameters.AddWithValue("tenant", view.Tenant);
        cmd.Parameters.AddWithValue("owner", view.Owner);
        cmd.Parameters.AddWithValue("kind", view.Kind);
        cmd.Parameters.AddWithValue("key", view.Key);
        cmd.Parameters.Add(new NpgsqlParameter("config", NpgsqlDbType.Jsonb) { Value = view.ConfigJson });
        cmd.Parameters.Add(new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = view.UpdatedAt.UtcDateTime });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioView>> ListAsync(
        string store, string tenant, string? kind = null, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {SelectColumns}
            FROM custodex.studio_view
            WHERE store = @store AND tenant = @tenant
              AND (@kind IS NULL OR kind = @kind)
            ORDER BY updated_at DESC
            """, conn);
        cmd.Parameters.AddWithValue("store", store);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)kind ?? DBNull.Value });

        var result = new List<StudioView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadView(reader));
        return result;
    }

    /// <inheritdoc />
    public async Task<StudioView?> GetAsync(
        string store, string tenant, string owner, string kind, string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {SelectColumns}
            FROM custodex.studio_view
            WHERE store = @store AND tenant = @tenant AND owner = @owner AND kind = @kind AND key = @key
            """, conn);
        cmd.Parameters.AddWithValue("store", store);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadView(reader) : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string store, string tenant, string owner, string kind, string key, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM custodex.studio_view
            WHERE store = @store AND tenant = @tenant AND owner = @owner AND kind = @kind AND key = @key
            """, conn);
        cmd.Parameters.AddWithValue("store", store);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static StudioView ReadView(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(6), TimeSpan.Zero));

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_ensured)
            return;

        await _ensureLock.WaitAsync(ct);
        try
        {
            if (_ensured)
                return;

            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                CREATE SCHEMA IF NOT EXISTS custodex;
                CREATE TABLE IF NOT EXISTS custodex.studio_view (
                    store text NOT NULL,
                    tenant text NOT NULL,
                    owner text NOT NULL,
                    kind text NOT NULL,
                    key text NOT NULL,
                    config jsonb NOT NULL,
                    updated_at timestamptz NOT NULL,
                    PRIMARY KEY (store, tenant, owner, kind, key)
                );
                """, conn);
            await cmd.ExecuteNonQueryAsync(ct);
            _ensured = true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }
}
