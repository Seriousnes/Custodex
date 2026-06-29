using Custodex.Abstractions;

using Microsoft.Data.Sqlite;

namespace Custodex.Storage.Sqlite;

/// <summary>
/// A SQLite unit of work. In <b>owned</b> mode the instance owns the connection and transaction and
/// commits or rolls them back on disposal. In <b>supplied</b> mode it borrows an external connection
/// and transaction supplied by the consuming application: <see cref="CommitAsync"/> is a no-op and
/// disposal releases nothing the external owner still controls.
/// </summary>
public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly bool _owned;
    private bool _committed;
    private bool _disposed;

    private SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction, bool owned)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
    }

    /// <summary>Gets the underlying SQLite connection.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Gets the underlying SQLite transaction.</summary>
    public SqliteTransaction Transaction { get; }

    internal static SqliteUnitOfWork Owned(SqliteConnection connection, SqliteTransaction transaction)
        => new(connection, transaction, owned: true);

    internal static SqliteUnitOfWork Supplied(SqliteConnection connection, SqliteTransaction transaction)
        => new(connection, transaction, owned: false);

    /// <summary>
    /// Resolves the live SQLite handles from an <see cref="IUnitOfWork"/> a store was handed.
    /// Throws <see cref="InvalidOperationException"/> when the unit of work is not a
    /// <see cref="SqliteUnitOfWork"/>.
    /// </summary>
    public static SqliteUnitOfWork From(IUnitOfWork uow)
        => uow as SqliteUnitOfWork
           ?? throw new InvalidOperationException(
               $"Expected a {nameof(SqliteUnitOfWork)} but got {uow.GetType().Name}. " +
               "The SQLite provider requires a SQLite unit of work.");

    /// <summary>
    /// In owned mode, commits the transaction. In supplied mode this is a no-op; the external owner
    /// is responsible for committing.
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
