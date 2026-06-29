using Custodex.Abstractions;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// A SQL Server unit of work. In <b>owned</b> mode the instance owns the connection and
/// transaction and commits or rolls them back on disposal. In <b>supplied</b> mode it
/// borrows an external connection and transaction supplied by the consuming application:
/// <see cref="CommitAsync"/> is a no-op and disposal releases nothing the external owner
/// still controls.
/// </summary>
public sealed class SqlServerUnitOfWork : IUnitOfWork
{
    private readonly bool _owned;
    private bool _committed;
    private bool _disposed;

    private SqlServerUnitOfWork(SqlConnection connection, SqlTransaction transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    /// <summary>Gets the underlying SQL Server connection.</summary>
    public SqlConnection Connection { get; }

    /// <summary>Gets the underlying SQL Server transaction.</summary>
    public SqlTransaction Transaction { get; }

    internal static SqlServerUnitOfWork Owned(SqlConnection connection, SqlTransaction transaction)
        => new(connection, transaction, owned: true);

    internal static SqlServerUnitOfWork Supplied(SqlConnection connection, SqlTransaction transaction)
        => new(connection, transaction, owned: false);

    /// <summary>
    /// Resolves the live SQL Server handles from an <see cref="IUnitOfWork"/> a store was handed.
    /// Throws <see cref="InvalidOperationException"/> when the unit of work is not a
    /// <see cref="SqlServerUnitOfWork"/>.
    /// </summary>
    public static SqlServerUnitOfWork From(IUnitOfWork uow)
        => uow as SqlServerUnitOfWork
           ?? throw new InvalidOperationException(
               $"Expected a {nameof(SqlServerUnitOfWork)} but got {uow.GetType().Name}. " +
               "The SQL Server provider requires a SQL Server unit of work.");

    /// <summary>
    /// In owned mode, commits the transaction. In supplied mode this is a no-op; the external
    /// owner is responsible for committing.
    /// </summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (!_owned)
            return;

        await Transaction.CommitAsync(ct);
        _committed = true;
    }

    /// <summary>
    /// In owned mode, rolls back any uncommitted work and disposes the transaction and connection.
    /// In supplied mode, this is a no-op; the external owner controls the lifetime of the handles.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (!_owned)
            return;

        if (!_committed)
            await Transaction.RollbackAsync();

        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
