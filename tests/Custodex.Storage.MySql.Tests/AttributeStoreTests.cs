using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.MySql.Tests;

[Collection("mysql")]
public class AttributeStoreTests(MySqlFixture fx) : IAsyncLifetime
{
    private MySqlUnitOfWorkFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new MySqlUnitOfWorkFactory(fx.ConnectionString);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = MySqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT IGNORE INTO stores (id) VALUES (@s)",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT IGNORE INTO tenants (store_id, tenant_id) VALUES (@s, @t)",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Set_then_get_round_trips_attributes()
    {
        var t = new TenantContext("s2", "tAttr");
        await SeedTenantAsync(t);
        var store = new MySqlAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "obj-1");

        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj,
                new Dictionary<string, object?> { ["is_flagged"] = true, ["weight_kg"] = 5400 }, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs.ShouldNotBeNull();
        attrs!["is_flagged"].ShouldNotBeNull();
        attrs.ContainsKey("weight_kg").ShouldBeTrue();
    }

    [Fact]
    public async Task Set_upserts_replacing_prior_attributes()
    {
        var t = new TenantContext("s2", "tAttr2");
        await SeedTenantAsync(t);
        var store = new MySqlAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "obj-2");

        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?> { ["v"] = 1 }, u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?> { ["v"] = 2 }, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs!["v"]!.ToString().ShouldBe("2");
    }

    [Fact]
    public async Task Get_returns_null_when_absent()
    {
        var t = new TenantContext("s2", "tAttr3");
        await SeedTenantAsync(t);
        var store = new MySqlAttributeStore(fx.ConnectionString);
        (await store.GetAsync(t, new EntityRef("resource", "missing"))).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_preserves_native_clr_value_types()
    {
        var t = new TenantContext("s2", "tFidelity");
        await SeedTenantAsync(t);
        var store = new MySqlAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "obj-typed");

        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?>
            {
                ["count"] = 5400L,
                ["flag"] = true,
                ["name"] = "abc",
                ["ratio"] = 1.5,
            }, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs.ShouldNotBeNull();
        attrs!["count"].ShouldBeOfType<long>().ShouldBe(5400L);
        attrs["flag"].ShouldBeOfType<bool>().ShouldBeTrue();
        attrs["name"].ShouldBeOfType<string>().ShouldBe("abc");
        attrs["ratio"].ShouldBeOfType<double>().ShouldBe(1.5);
    }
}
