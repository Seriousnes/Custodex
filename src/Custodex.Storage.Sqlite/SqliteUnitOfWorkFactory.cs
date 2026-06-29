using System.Data.Common;

using Custodex.Abstractions;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// Creates and enlists <see cref="SqliteUnitOfWork"/> instances over a SQLite connection.
/// <see cref="BeginAsync"/> opens a fresh connection and transaction that the engine owns.
/// <see cref="Enlist"/> borrows an externally-owned connection and transaction so engine writes
/// participate in the consuming application's transaction without the engine committing or closing
/// either handle.
/// </summary>
public sealed class SqliteUnitOfWorkFactory(string connectionString) : IUnitOfWorkFactory
{
    private readonly string _connectionString = connectionString;

    /// <summary>
    /// Owned mode: opens a fresh connection, begins a transaction, and returns a unit of work that
    /// commits and disposes both handles on <see cref="SqliteUnitOfWork.CommitAsync"/> and
    /// <see cref="SqliteUnitOfWork.DisposeAsync"/> respectively.
    /// </summary>
    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = await SqliteConnections.OpenAsync(_connectionString, ct);
        try
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            return SqliteUnitOfWork.Owned(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Supplied mode: enlists in the consuming application's own connection and transaction so engine
    /// writes commit atomically inside its unit of work. The engine never commits, rolls back, or
    /// closes these handles. Throws <see cref="InvalidOperationException"/> when either handle is not
    /// a SQLite type, as the SQLite provider requires SQLite-specific types.
    /// </summary>
    public IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction)
    {
        if (connection is not SqliteConnection sqliteConn)
            throw new InvalidOperationException(
                $"The SQLite provider requires a {nameof(SqliteConnection)}, got {connection.GetType().Name}.");
        if (transaction is not SqliteTransaction sqliteTx)
            throw new InvalidOperationException(
                $"The SQLite provider requires a {nameof(SqliteTransaction)}, got {transaction.GetType().Name}.");

        return SqliteUnitOfWork.Supplied(sqliteConn, sqliteTx);
    }
}
