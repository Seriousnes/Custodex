using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

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

    private static Schema ViewerSchema() => new SchemaBuilder("v1")
        .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
        .Build();

    private async Task ActivateSchemaAsync(NpgsqlUnitOfWorkFactory factory, NpgsqlSchemaStore schemas, string store)
    {
        await using var u = await factory.BeginAsync();
        await schemas.SetActiveAsync(store, ViewerSchema(), u);
        await u.CommitAsync();
    }

    private static CheckRequest ViewCheck(TenantContext t) => new(
        t, new EntityRef("doc", "d1"), "view", new SubjectRef("user", "alice"),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>()));

    private static ListObjectsRequest ViewListObjects(TenantContext t) => new(
        t, new SubjectRef("user", "alice"), "doc", "view",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>()));

    private async Task<(NpgsqlUnitOfWorkFactory factory, NpgsqlRelationStore relations, NpgsqlCteAuthorizer auth, TenantContext t)>
        SetupAuthorizerAsync(string store)
    {
        await using (var seed = await fx.OpenAsync())
            await MigrationRunner.ApplyAsync(seed);
        await SeedTenantAsync(store, "t1");

        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        await ActivateSchemaAsync(factory, schemas, store);

        var auth = new NpgsqlCteAuthorizer(fx.ConnectionString, schemas, attributes, new NullConditionEvaluator());
        return (factory, relations, auth, new TenantContext(store, "t1"));
    }

    private static RelationTuple ViewerTuple => new(new EntityRef("doc", "d1"), "viewer", new SubjectRef("user", "alice"));

    [Fact]
    public async Task Check_inside_the_supplied_transaction_observes_a_tuple_written_earlier()
    {
        var (factory, relations, auth, t) = await SetupAuthorizerAsync("ryw-check");

        await using var appConn = new NpgsqlConnection(fx.RawConnectionString);
        await appConn.OpenAsync();
        await using var appTx = await appConn.BeginTransactionAsync();

        await using var uow = factory.Enlist(appConn, appTx);
        await relations.WriteAsync(t, [ViewerTuple], [], uow);

        (await auth.OnUnitOfWork(uow).CheckAsync(ViewCheck(t))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(ViewCheck(t))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ListObjects_inside_the_supplied_transaction_includes_the_written_object()
    {
        var (factory, relations, auth, t) = await SetupAuthorizerAsync("ryw-list");

        await using var appConn = new NpgsqlConnection(fx.RawConnectionString);
        await appConn.OpenAsync();
        await using var appTx = await appConn.BeginTransactionAsync();

        await using var uow = factory.Enlist(appConn, appTx);
        await relations.WriteAsync(t, [ViewerTuple], [], uow);

        (await auth.OnUnitOfWork(uow).ListObjectsAsync(ViewListObjects(t))).ObjectIds.ShouldContain("d1");
        (await auth.ListObjectsAsync(ViewListObjects(t))).ObjectIds.ShouldNotContain("d1");
    }

    [Fact]
    public async Task After_the_supplied_transaction_rolls_back_a_fresh_check_reflects_no_write()
    {
        var (factory, relations, auth, t) = await SetupAuthorizerAsync("ryw-rollback");

        await using var appConn = new NpgsqlConnection(fx.RawConnectionString);
        await appConn.OpenAsync();
        var appTx = await appConn.BeginTransactionAsync();

        var uow = factory.Enlist(appConn, appTx);
        await relations.WriteAsync(t, [ViewerTuple], [], uow);
        (await auth.OnUnitOfWork(uow).CheckAsync(ViewCheck(t))).Allowed.ShouldBeTrue();
        await uow.DisposeAsync();

        await appTx.RollbackAsync();
        await appTx.DisposeAsync();

        (await auth.CheckAsync(ViewCheck(t))).Allowed.ShouldBeFalse();
    }
}
