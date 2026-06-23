using Relkit.Abstractions;
using Relkit.Storage.InMemory;
using Shouldly;

namespace Relkit.Storage.InMemory.Tests;

public class UnitOfWorkTests
{
    [Fact]
    public async Task Factory_yields_a_committable_disposable_unit_of_work()
    {
        IUnitOfWorkFactory factory = new NoOpUnitOfWorkFactory();

        await using var uow = await factory.BeginAsync();
        await uow.CommitAsync();   // no-op, must not throw
        uow.ShouldBeAssignableTo<IUnitOfWork>();
    }
}
