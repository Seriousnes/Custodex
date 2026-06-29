using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests;

public class ValueFidelityTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private readonly SqliteUnitOfWorkFactory _factory = new(fx.ConnectionString);

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = SqliteUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Attribute_values_round_trip_to_their_clr_primitive_types()
    {
        var t = new TenantContext("fidelity", "tF");
        await SeedTenantAsync(t);
        var store = new SqliteAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "obj-fidelity");

        await using (var u = await _factory.BeginAsync())
        {
            await store.SetAsync(t, obj, new Dictionary<string, object?>
            {
                ["count"] = 9_999_999_999L,
                ["enabled"] = true,
                ["label"] = "amber",
                ["ratio"] = 2.5,
            }, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs.ShouldNotBeNull();

        attrs!["count"].ShouldBeOfType<long>().ShouldBe(9_999_999_999L);
        attrs["enabled"].ShouldBeOfType<bool>().ShouldBeTrue();
        attrs["label"].ShouldBeOfType<string>().ShouldBe("amber");
        attrs["ratio"].ShouldBeOfType<double>().ShouldBe(2.5);
    }
}
