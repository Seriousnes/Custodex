using Dapper;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class OwnedTransactionTests(PostgresFixture fx)
{
    private const string InsertTuple = """
        INSERT INTO relation_tuples
            (store_id, tenant_id, object_type, object_id, relation, subject_type, subject_id)
        VALUES (@store, @tenant, 'resource', @oid, 'owner', 'user', 'user-a')
        """;

    private static async Task SeedTenantAsync(NpgsqlUnitOfWork uow, string store, string tenant)
    {
        await uow.Connection.ExecuteAsync(
            "INSERT INTO stores (id) VALUES (@store) ON CONFLICT DO NOTHING",
            new { store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@store, @tenant) ON CONFLICT DO NOTHING",
            new { store, tenant }, uow.Transaction);
    }

    [Fact]
    public async Task Commit_persists_the_write()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        const string store = "owned-commit";
        const string tenant = "t1";

        await using (var u = await factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await SeedTenantAsync(uow, store, tenant);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "obj-001" }, uow.Transaction);
            await u.CommitAsync();
        }

        await using var verify = await fx.OpenAsync();
        var count = await verify.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relation_tuples WHERE store_id = @store AND object_id = 'obj-001'",
            new { store });
        count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_without_commit_rolls_back_the_write()
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        const string store = "owned-rollback";
        const string tenant = "t1";

        await using (var u = await factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await SeedTenantAsync(uow, store, tenant);
            await u.CommitAsync();
        }

        await using (var u = await factory.BeginAsync())
        {
            var uow = NpgsqlUnitOfWork.From(u);
            await uow.Connection.ExecuteAsync(InsertTuple,
                new { store, tenant, oid = "obj-002" }, uow.Transaction);
        }

        await using var verify = await fx.OpenAsync();
        var count = await verify.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM relation_tuples WHERE store_id = @store AND object_id = 'obj-002'",
            new { store });
        count.ShouldBe(0);
    }
}
