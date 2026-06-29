using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.MySql.Tests;

[Collection("mysql")]
public class CrossTenantIsolationTests(MySqlFixture fx) : IAsyncLifetime
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
    public async Task Tuple_written_in_tenant_A_is_invisible_in_tenant_B()
    {
        var a = new TenantContext("store-a", "t-a");
        var b = new TenantContext("store-a", "t-b");
        await SeedTenantAsync(a);
        await SeedTenantAsync(b);

        var store = new MySqlRelationStore(fx.ConnectionString);
        var tuple = new RelationTuple(
            new EntityRef("resource", "obj-1"), "editor", new SubjectRef("user", "pat"));

        await using (var u = await _factory.BeginAsync())
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
