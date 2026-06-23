using Relkit.Abstractions;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Storage.InMemory.Tests;

public class AttributeStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly TenantContext T2 = new("zoo", "t2");
    private static readonly NoOpUnitOfWork Uow = new();
    private static readonly EntityRef Animal = new("animal", "EL-001");

    [Fact]
    public async Task Missing_object_returns_null()
    {
        (await new InMemoryAttributeStore().GetAsync(T1, Animal)).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_attribute_bag()
    {
        var store = new InMemoryAttributeStore();
        await store.SetAsync(T1, Animal, new Dictionary<string, object?> { ["weight"] = 42 }, Uow);

        var attrs = await store.GetAsync(T1, Animal);

        attrs!["weight"].ShouldBe(42);
    }

    [Fact]
    public async Task Stored_bag_is_isolated_from_later_caller_mutation()
    {
        var store = new InMemoryAttributeStore();
        var input = new Dictionary<string, object?> { ["weight"] = 42 };
        await store.SetAsync(T1, Animal, input, Uow);
        input["weight"] = 99;   // mutate the caller's dictionary after the write

        (await store.GetAsync(T1, Animal))!["weight"].ShouldBe(42);
    }

    [Fact]
    public async Task Attributes_do_not_leak_across_tenants()
    {
        var store = new InMemoryAttributeStore();
        await store.SetAsync(T1, Animal, new Dictionary<string, object?> { ["weight"] = 42 }, Uow);

        (await store.GetAsync(T2, Animal)).ShouldBeNull();
    }
}
