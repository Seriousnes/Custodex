using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

/// <summary>An <see cref="IUnitOfWork"/> for the in-memory providers, whose writes apply immediately, so committing and disposing do nothing. Suited to tests and local development.</summary>
public sealed class NoOpUnitOfWork : IUnitOfWork
{
    /// <inheritdoc/>
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An <see cref="IUnitOfWorkFactory"/> that vends <see cref="NoOpUnitOfWork"/> instances for the in-memory providers.</summary>
public sealed class NoOpUnitOfWorkFactory : IUnitOfWorkFactory
{
    /// <inheritdoc/>
    public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default) =>
        Task.FromResult<IUnitOfWork>(new NoOpUnitOfWork());
}
