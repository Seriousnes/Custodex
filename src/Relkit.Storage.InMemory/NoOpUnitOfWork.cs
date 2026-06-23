using Relkit.Abstractions;

namespace Relkit.Storage.InMemory;

public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NoOpUnitOfWorkFactory : IUnitOfWorkFactory
{
    public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default) =>
        Task.FromResult<IUnitOfWork>(new NoOpUnitOfWork());
}
