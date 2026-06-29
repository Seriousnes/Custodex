using Custodex.Abstractions;
using Custodex.Core;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class SchemaStoreTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);

    private static async Task SeedStoreAsync(SqlServerUnitOfWorkFactory factory, string store) =>
        await Seed.TenantAsync(factory, new TenantContext(store, store));

    private static Schema SchemaV(string version) => new SchemaBuilder(version)
        .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
        .Build();

    [Fact]
    public async Task Set_then_get_active_round_trips_schema()
    {
        const string store = "schema-s1";
        await SeedStoreAsync(Factory, store);

        await using (var u = await Factory.BeginAsync())
        {
            await new SqlServerSchemaStore(fx.ConnectionString).SetActiveAsync(store, SchemaV("v1"), u);
            await u.CommitAsync();
        }

        var active = await new SqlServerSchemaStore(fx.ConnectionString).GetActiveAsync(store);
        active.ShouldNotBeNull();
        active!.Version.ShouldBe("v1");
        active.Types.Single().Permissions.Single().Name.ShouldBe("view");
    }

    [Fact]
    public async Task Set_active_makes_only_one_version_active()
    {
        const string store = "schema-s2";
        await SeedStoreAsync(Factory, store);
        var store2 = new SqlServerSchemaStore(fx.ConnectionString);

        await using (var u = await Factory.BeginAsync())
        {
            await store2.SetActiveAsync(store, SchemaV("v1"), u);
            await u.CommitAsync();
        }
        await using (var u = await Factory.BeginAsync())
        {
            await store2.SetActiveAsync(store, SchemaV("v2"), u);
            await u.CommitAsync();
        }

        (await store2.GetActiveAsync(store))!.Version.ShouldBe("v2");
    }

    [Fact]
    public async Task Get_active_returns_null_when_none_set()
    {
        const string store = "schema-empty";
        await SeedStoreAsync(Factory, store);
        (await new SqlServerSchemaStore(fx.ConnectionString).GetActiveAsync(store)).ShouldBeNull();
    }
}
