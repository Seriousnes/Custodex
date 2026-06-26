using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

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
        var world = TestWorld.New();
        var store = world.Tenant.Store;
        var schemaStore = new InMemorySchemaStore();
        await schemaStore.SetActiveAsync(store, SchemaV(world.Version), Uow);

        (await schemaStore.GetActiveAsync(store))!.Version.ShouldBe(world.Version);
    }

    [Fact]
    public async Task Set_replaces_the_previous_active_schema()
    {
        var world = TestWorld.New();
        var store = world.Tenant.Store;
        var schemaStore = new InMemorySchemaStore();
        await schemaStore.SetActiveAsync(store, SchemaV("v1"), Uow);
        await schemaStore.SetActiveAsync(store, SchemaV("v2"), Uow);

        (await schemaStore.GetActiveAsync(store))!.Version.ShouldBe("v2");
    }
}
