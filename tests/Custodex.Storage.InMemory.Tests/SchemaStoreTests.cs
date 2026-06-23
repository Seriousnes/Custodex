using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class SchemaStoreTests
{
    private static readonly NoOpUnitOfWork Uow = new();

    private static Schema SchemaV(string version) => new(version, [], []);

    [Fact]
    public async Task Missing_store_returns_null()
    {
        (await new InMemorySchemaStore().GetActiveAsync("nope")).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_active_schema()
    {
        var store = new InMemorySchemaStore();
        await store.SetActiveAsync("zoo", SchemaV("v1"), Uow);

        (await store.GetActiveAsync("zoo"))!.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Set_replaces_the_previous_active_schema()
    {
        var store = new InMemorySchemaStore();
        await store.SetActiveAsync("zoo", SchemaV("v1"), Uow);
        await store.SetActiveAsync("zoo", SchemaV("v2"), Uow);

        (await store.GetActiveAsync("zoo"))!.Version.ShouldBe("v2");
    }
}
