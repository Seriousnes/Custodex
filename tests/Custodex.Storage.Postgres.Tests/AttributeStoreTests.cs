using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class AttributeStoreTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Set_then_get_round_trips_attributes()
    {
        var t = new TenantContext("s2", "tAttr");
        await SeedTenantAsync(t);
        var store = new NpgsqlAttributeStore(fx.ConnectionString);
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
        var store = new NpgsqlAttributeStore(fx.ConnectionString);
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
        var store = new NpgsqlAttributeStore(fx.ConnectionString);
        (await store.GetAsync(t, new EntityRef("resource", "missing"))).ShouldBeNull();
    }
}
