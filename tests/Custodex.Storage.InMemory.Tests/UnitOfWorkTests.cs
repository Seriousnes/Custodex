using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class UnitOfWorkTests
{
    [Fact]
    public async Task Factory_yields_a_committable_disposable_unit_of_work()
    {
        var factory = new NoOpUnitOfWorkFactory();

        await using var uow = await factory.BeginAsync();
        await uow.CommitAsync();
        uow.ShouldBeAssignableTo<IUnitOfWork>();
    }
}
