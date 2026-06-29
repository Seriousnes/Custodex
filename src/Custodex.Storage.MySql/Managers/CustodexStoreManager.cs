using Custodex.Abstractions;

using Dapper;

using MySqlConnector;

namespace Custodex.Storage.MySql.Managers;

/// <summary>
/// Concrete <see cref="IStoreManager"/> backed by MySQL.
/// Inserts a row into the <c>stores</c> table; a duplicate store id is silently ignored.
/// </summary>
public sealed class CustodexStoreManager(string connectionString) : IStoreManager
{
    private readonly string _cs = connectionString;

    /// <inheritdoc />
    public async Task CreateStoreAsync(string store, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_cs);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO stores (id) VALUES (@store)",
            new { store }, cancellationToken: ct));
    }
}
