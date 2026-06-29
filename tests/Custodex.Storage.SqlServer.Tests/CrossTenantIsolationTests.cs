using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests;

[Collection("sqlserver")]
public class CrossTenantIsolationTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);

    [Fact]
    public async Task Tuple_written_in_tenant_A_is_invisible_in_tenant_B()
    {
        var a = new TenantContext("iso-store-a", "t-a");
        var b = new TenantContext("iso-store-a", "t-b");
        await Seed.TenantAsync(Factory, a);
        await Seed.TenantAsync(Factory, b);

        var store = new SqlServerRelationStore(fx.ConnectionString);
        var tuple = new RelationTuple(
            new EntityRef("resource", "obj-1"), "editor", new SubjectRef("user", "pat"));

        await using (var u = await Factory.BeginAsync())
        {
            await store.WriteAsync(a, [tuple], [], u);
            await u.CommitAsync();
        }

        (await store.GetByObjectAsync(b, new EntityRef("resource", "obj-1"), "editor")).ShouldBeEmpty();
        (await store.GetBySubjectAsync(b, new SubjectRef("user", "pat"))).ShouldBeEmpty();

        (await store.GetByObjectAsync(a, new EntityRef("resource", "obj-1"), "editor"))
            .ShouldHaveSingleItem();
    }
}
