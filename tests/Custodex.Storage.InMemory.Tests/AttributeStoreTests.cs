using Custodex.Abstractions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Storage.InMemory.Tests;

public class AttributeStoreTests
{
    private static readonly NoOpUnitOfWork Uow = new();

    [Fact]
    public async Task Missing_object_returns_null()
    {
        var world = TestWorld.New();
        var obj = world.Object(world.EntityType(), world.ObjectId());

        (await new InMemoryAttributeStore().GetAsync(world.Tenant, obj)).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_attribute_bag()
    {
        var world = TestWorld.New();
        var obj = world.Object(world.EntityType(), world.ObjectId());
        var key = world.ParamName();
        var store = new InMemoryAttributeStore();
        await store.SetAsync(world.Tenant, obj, new Dictionary<string, object?> { [key] = 42 }, Uow);

        var attrs = await store.GetAsync(world.Tenant, obj);

        attrs![key].ShouldBe(42);
    }

    [Fact]
    public async Task Stored_bag_is_isolated_from_later_caller_mutation()
    {
        var world = TestWorld.New();
        var obj = world.Object(world.EntityType(), world.ObjectId());
        var key = world.ParamName();
        var store = new InMemoryAttributeStore();
        var input = new Dictionary<string, object?> { [key] = 42 };
        await store.SetAsync(world.Tenant, obj, input, Uow);
        input[key] = 99;   // mutate the caller's dictionary after the write

        (await store.GetAsync(world.Tenant, obj))![key].ShouldBe(42);
    }

    [Fact]
    public async Task Attributes_do_not_leak_across_tenants()
    {
        var world = TestWorld.New();
        var obj = world.Object(world.EntityType(), world.ObjectId());
        var key = world.ParamName();
        var t1 = world.Tenant;
        var t2 = new TenantContext(world.Tenant.Store, world.EntityType());
        var store = new InMemoryAttributeStore();
        await store.SetAsync(t1, obj, new Dictionary<string, object?> { [key] = 42 }, Uow);

        (await store.GetAsync(t2, obj)).ShouldBeNull();
    }
}
