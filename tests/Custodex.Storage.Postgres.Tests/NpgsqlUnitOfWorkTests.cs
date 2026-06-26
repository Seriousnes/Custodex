using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

public class NpgsqlUnitOfWorkTests
{
    private sealed class FakeUow : IUnitOfWork
    {
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void From_rejects_a_non_npgsql_unit_of_work()
    {
        Should.Throw<InvalidOperationException>(() => NpgsqlUnitOfWork.From(new FakeUow()));
    }
}
