using System.Data.Common;

using Custodex.Abstractions;

using MySqlConnector;

namespace Custodex.Storage.MySql;

/// <summary>
/// Creates and enlists <see cref="MySqlUnitOfWork"/> instances over a MySQL connection.
/// <see cref="BeginAsync"/> opens a fresh connection and transaction that the engine owns.
/// <see cref="Enlist"/> borrows an externally-owned connection and transaction so engine
/// writes participate in the consuming application's transaction without the engine committing
/// or closing either handle.
/// </summary>
public sealed class MySqlUnitOfWorkFactory(string connectionString) : IUnitOfWorkFactory
{
    private readonly string _connectionString = connectionString;

    /// <summary>
    /// Owned mode: opens a fresh connection, begins a transaction, and returns a unit of work
    /// that commits and disposes both handles on <see cref="MySqlUnitOfWork.CommitAsync"/> and
    /// <see cref="MySqlUnitOfWork.DisposeAsync"/> respectively.
    /// </summary>
    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var transaction = await connection.BeginTransactionAsync(ct);
            return MySqlUnitOfWork.Owned(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Supplied mode: enlists in the consuming application's own connection and transaction so
    /// engine writes commit atomically inside its unit of work. The engine never commits, rolls
    /// back, or closes these handles. Throws <see cref="InvalidOperationException"/> when either
    /// handle is not a MySqlConnector type, as the MySQL provider requires those types.
    /// </summary>
    public IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction)
    {
        if (connection is not MySqlConnection myConn)
            throw new InvalidOperationException(
                $"The MySQL provider requires a {nameof(MySqlConnection)}, got {connection.GetType().Name}.");
        if (transaction is not MySqlTransaction myTx)
            throw new InvalidOperationException(
                $"The MySQL provider requires a {nameof(MySqlTransaction)}, got {transaction.GetType().Name}.");

        return MySqlUnitOfWork.Supplied(myConn, myTx);
    }
}
