using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class AttributeStoreTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);

    [Fact]
    public async Task Set_then_get_round_trips_attributes()
    {
        var t = new TenantContext("attr-s2", "tAttr");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "obj-1");

        await using (var u = await Factory.BeginAsync())
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
        var t = new TenantContext("attr-s2", "tAttr2");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "obj-2");

        await using (var u = await Factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?> { ["v"] = 1 }, u);
            await u.CommitAsync();
        }
        await using (var u = await Factory.BeginAsync())
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
        var t = new TenantContext("attr-s2", "tAttr3");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerAttributeStore(fx.ConnectionString);
        (await store.GetAsync(t, new EntityRef("resource", "missing"))).ShouldBeNull();
    }
}
