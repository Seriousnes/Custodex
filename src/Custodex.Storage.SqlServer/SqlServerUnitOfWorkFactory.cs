using System.Data.Common;

using Custodex.Abstractions;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Creates and enlists <see cref="SqlServerUnitOfWork"/> instances over a SQL Server connection.
/// <see cref="BeginAsync"/> opens a fresh connection and transaction that the engine owns.
/// <see cref="Enlist"/> borrows an externally-owned connection and transaction so engine
/// writes participate in the consuming application's transaction without the engine committing
/// or closing either handle.
/// </summary>
public sealed class SqlServerUnitOfWorkFactory(string connectionString) : IUnitOfWorkFactory
{
    private readonly string _connectionString = connectionString;

    /// <summary>
    /// Owned mode: opens a fresh connection, begins a transaction, and returns a unit of work
    /// that commits and disposes both handles on <see cref="SqlServerUnitOfWork.CommitAsync"/> and
    /// <see cref="SqlServerUnitOfWork.DisposeAsync"/> respectively.
    /// </summary>
    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            return SqlServerUnitOfWork.Owned(connection, transaction);
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
    /// handle is not a SQL Server type, as the provider requires SQL Server-specific types.
    /// </summary>
    public IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction)
    {
        if (connection is not SqlConnection sqlConn)
            throw new InvalidOperationException(
                $"The SQL Server provider requires a {nameof(SqlConnection)}, got {connection.GetType().Name}.");
        if (transaction is not SqlTransaction sqlTx)
            throw new InvalidOperationException(
                $"The SQL Server provider requires a {nameof(SqlTransaction)}, got {transaction.GetType().Name}.");

        return SqlServerUnitOfWork.Supplied(sqlConn, sqlTx);
    }
}
