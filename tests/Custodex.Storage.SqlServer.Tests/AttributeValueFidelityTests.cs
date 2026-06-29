using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class AttributeValueFidelityTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);

    [Fact]
    public async Task Mixed_value_types_round_trip_to_their_clr_types()
    {
        var t = new TenantContext("fidelity-s", "tFid");
        await Seed.TenantAsync(Factory, t);
        var store = new SqlServerAttributeStore(fx.ConnectionString);
        var obj = new EntityRef("resource", "mixed-1");

        var written = new Dictionary<string, object?>
        {
            ["count"] = 42L,
            ["enabled"] = true,
            ["label"] = "active",
            ["ratio"] = 3.5,
        };

        await using (var u = await Factory.BeginAsync())
        {
            await store.SetAsync(t, obj, written, u);
            await u.CommitAsync();
        }

        var attrs = await store.GetAsync(t, obj);
        attrs.ShouldNotBeNull();

        attrs!["count"].ShouldBeOfType<long>().ShouldBe(42L);
        attrs["enabled"].ShouldBeOfType<bool>().ShouldBeTrue();
        attrs["label"].ShouldBeOfType<string>().ShouldBe("active");
        attrs["ratio"].ShouldBeOfType<double>().ShouldBe(3.5);
    }
}
