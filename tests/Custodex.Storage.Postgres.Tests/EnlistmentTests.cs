using Custodex.Abstractions;
using Dapper;
using Npgsql;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class EnlistmentTests(PostgresFixture fx)
{
    private const string InsertTuple = """
        INSERT INTO relation_tuples
            (store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id)
        VALUES (@store, @tenant, 'resource', @oid, 'owner', 'user', 'user-a')
        """;

    private static async Task<long> CountAsync(NpgsqlConnection conn, string store, string oid) =>
        await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relation_tuples WHERE store_id = @store AND object_id = @oid",
            new { store, oid });

    private async Task SeedTenantAsync(string store, string tenant)
    {
        await using var conn = await fx.OpenAsync();
        await conn.ExecuteAsync("INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING", new { store });
        await conn.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING",
            new { store, tenant });
    }

    [Fact]
    public async Task Engine_write_is_invisible_until_the_external_owner_commits()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        const string store = "enlist-commit";
        const string tenant = "t1";
        await SeedTenantAsync(store, tenant);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var appConn = await fx.OpenAsync();
        await using var appTx = await appConn.BeginTransactionAsync();

        await using (var u = factory.Enlist(appConn, appTx))
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "obj-100" }, uow.Transaction);
            await u.CommitAsync();
        }

        await using (var other = await fx.OpenAsync())
            (await CountAsync(other, store, "obj-100")).ShouldBe(0);

        await appTx.CommitAsync();

        await using (var other = await fx.OpenAsync())
            (await CountAsync(other, store, "obj-100")).ShouldBe(1);

        appConn.State.ShouldBe(System.Data.ConnectionState.Open);
        (await CountAsync(appConn, store, "obj-100")).ShouldBe(1);
    }

    [Fact]
    public async Task Store_write_enlisted_on_a_consumer_connection_targets_the_custodex_schema()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        const string store = "enlist-raw";
        const string tenant = "t1";
        await SeedTenantAsync(store, tenant);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var t = new TenantContext(store, tenant);
        var tuple = new RelationTuple(new EntityRef("resource", "obj-300"), "owner", new SubjectRef("user", "user-a"));

        await using var appConn = new NpgsqlConnection(fx.RawConnectionString);
        await appConn.OpenAsync();
        await using var appTx = await appConn.BeginTransactionAsync();

        await using (var u = factory.Enlist(appConn, appTx))
        {
            await relations.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }
        await appTx.CommitAsync();

        await using var other = await fx.OpenAsync();
        (await CountAsync(other, store, "obj-300")).ShouldBe(1);
    }

    [Fact]
    public async Task Engine_write_is_discarded_when_the_external_owner_rolls_back()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        const string store = "enlist-rollback";
        const string tenant = "t1";
        await SeedTenantAsync(store, tenant);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);

        await using var appConn = await fx.OpenAsync();
        var appTx = await appConn.BeginTransactionAsync();

        await using (var u = factory.Enlist(appConn, appTx))
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "obj-200" }, uow.Transaction);
            await u.CommitAsync();
        }

        await appTx.RollbackAsync();
        await appTx.DisposeAsync();

        await using var other = await fx.OpenAsync();
        (await CountAsync(other, store, "obj-200")).ShouldBe(0);

        appConn.State.ShouldBe(System.Data.ConnectionState.Open);
    }
}
