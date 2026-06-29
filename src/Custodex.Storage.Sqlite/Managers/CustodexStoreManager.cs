using Custodex.Abstractions;

using Dapper;

namespace Custodex.Storage.Sqlite.Managers;

/// <summary>
/// Concrete <see cref="IStoreManager"/> backed by SQLite.
/// Inserts a row into the <c>stores</c> table; a duplicate store id is silently ignored.
/// </summary>
public sealed class CustodexStoreManager(string connectionString) : IStoreManager
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        await using var conn = await SqliteConnections.OpenAsync(_cs, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING",
            new { store }, cancellationToken: ct));
    }
}
