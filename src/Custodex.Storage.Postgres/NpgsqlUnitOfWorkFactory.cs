using System.Data.Common;

using Custodex.Abstractions;

using Npgsql;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Creates and enlists <see cref="NpgsqlUnitOfWork"/> instances over a Postgres connection.
/// <see cref="BeginAsync"/> opens a fresh connection and transaction that the engine owns.
/// <see cref="Enlist"/> borrows an externally-owned connection and transaction so engine
/// writes participate in the consuming application's transaction without the engine committing
/// or closing either handle.
/// </summary>
public sealed class NpgsqlUnitOfWorkFactory(string connectionString) : IUnitOfWorkFactory
{
    private readonly string _connectionString = CustodexSchema.Apply(connectionString);

    /// <summary>
    /// Owned mode: opens a fresh connection, begins a transaction, and returns a unit of work
    /// that commits and disposes both handles on <see cref="NpgsqlUnitOfWork.CommitAsync"/> and
    /// <see cref="NpgsqlUnitOfWork.DisposeAsync"/> respectively.
    /// </summary>
    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var transaction = await connection.BeginTransactionAsync(ct);
            return NpgsqlUnitOfWork.Owned(connection, transaction);
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
    /// handle is not an Npgsql type, as the Postgres provider requires Npgsql-specific types for
    /// correct jsonb mapping.
    /// </summary>
    public IUnitOfWork Enlist(DbConnection connection, DbTransaction transaction)
    {
        if (connection is not NpgsqlConnection npgConn)
            throw new InvalidOperationException(
                $"The Postgres provider requires an {nameof(NpgsqlConnection)}, got {connection.GetType().Name}.");
        if (transaction is not NpgsqlTransaction npgTx)
            throw new InvalidOperationException(
                $"The Postgres provider requires an {nameof(NpgsqlTransaction)}, got {transaction.GetType().Name}.");

        return NpgsqlUnitOfWork.Supplied(npgConn, npgTx);
    }
}
