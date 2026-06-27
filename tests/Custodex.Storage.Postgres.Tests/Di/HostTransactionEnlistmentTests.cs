using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Di;

[Collection("postgres")]
public class HostTransactionEnlistmentTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Schema BuildSchema() =>
        new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddCustodex().UsePostgres(connectionString);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Host_enlisted_writes_become_durable_only_when_the_host_commits()
    {
        var provider = BuildProvider(fx.ConnectionString);
        var factory = provider.GetRequiredService<IUnitOfWorkFactory>();
        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "host-commit";
        var t = new TenantContext(store, "tenant-1");
        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);

        var tuple = new RelationTuple(new EntityRef("doc", "d1"), "viewer", new SubjectRef("user", "alice"));
        var obj = new EntityRef("doc", "d1");
        Dictionary<string, object?> attrs = new() { ["sensitive"] = true, ["weight"] = 7.5 };

        await using var hostConn = new NpgsqlConnection(fx.RawConnectionString);
        await hostConn.OpenAsync();
        await using var hostTx = await hostConn.BeginTransactionAsync();

        await using (var uow = factory.Enlist(hostConn, hostTx))
        {
            await schemas.SetActiveSchemaAsync(store, BuildSchema(), uow);
            await relations.WriteTuplesAsync(t, "host-actor", [tuple], uow);
            await relations.WriteAttributesAsync(t, "host-actor", obj, attrs, uow);
        }

        (await relations.ReadTuplesAsync(t, new TupleFilter(ObjectType: "doc"))).ShouldBeEmpty();

        await hostTx.CommitAsync();

        (await relations.ReadTuplesAsync(t, new TupleFilter(ObjectType: "doc")))
            .ShouldHaveSingleItem().Object.Id.ShouldBe("d1");

        (await schemas.GetActiveSchemaAsync(store))!.Version.ShouldBe("v1");

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());
        (await authorizer.CheckAsync(new CheckRequest(t, obj, "view", new SubjectRef("user", "alice"), ctx)))
            .Allowed.ShouldBeTrue();

        var log = await relations.ReadChangeLogAsync(t, new ChangeLogFilter());
        log.Count(e => e.Operation == "write").ShouldBe(2);
    }

    [Fact]
    public async Task Host_rollback_discards_every_enlisted_engine_write()
    {
        var provider = BuildProvider(fx.ConnectionString);
        var factory = provider.GetRequiredService<IUnitOfWorkFactory>();
        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "host-rollback";
        var t = new TenantContext(store, "tenant-1");
        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);
        await schemas.SetActiveSchemaAsync(store, BuildSchema());

        var tuple = new RelationTuple(new EntityRef("doc", "d1"), "viewer", new SubjectRef("user", "alice"));
        var obj = new EntityRef("doc", "d1");
        Dictionary<string, object?> attrs = new() { ["sensitive"] = true };

        await using var hostConn = new NpgsqlConnection(fx.RawConnectionString);
        await hostConn.OpenAsync();
        var hostTx = await hostConn.BeginTransactionAsync();

        await using (var uow = factory.Enlist(hostConn, hostTx))
        {
            await relations.WriteTuplesAsync(t, "host-actor", [tuple], uow);
            await relations.WriteAttributesAsync(t, "host-actor", obj, attrs, uow);
        }

        await hostTx.RollbackAsync();
        await hostTx.DisposeAsync();

        (await relations.ReadTuplesAsync(t, new TupleFilter(ObjectType: "doc"))).ShouldBeEmpty();
        (await relations.ReadChangeLogAsync(t, new ChangeLogFilter())).ShouldBeEmpty();

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());
        (await authorizer.CheckAsync(new CheckRequest(t, obj, "view", new SubjectRef("user", "alice"), ctx)))
            .Allowed.ShouldBeFalse();

        hostConn.State.ShouldBe(System.Data.ConnectionState.Open);
    }
}
