using Microsoft.Extensions.DependencyInjection;
using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres;
using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Di;

[Collection("postgres")]
public class EndToEndSampleTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Define_schema_write_tuples_then_check_and_list()
    {
        var schemaBuilder = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("asset", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")));

        var services = new ServiceCollection();
        services.AddCustodex().UsePostgres(fx.ConnectionString).UseSchema(schemaBuilder);
        var provider = services.BuildServiceProvider();

        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "e2e";
        var t = new TenantContext(store, "tenant-1");

        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);
        await tenants.CreateTenantAsync(new TenantContext(store, store));
        await schemas.SetActiveSchemaAsync(store, schemaBuilder.Build());

        await relations.WriteTuplesAsync(t, "admin-a",
        [
            new RelationTuple(new EntityRef("asset", "ka"), "editor", new SubjectRef("group", "herd", "member")),
            new RelationTuple(new EntityRef("asset", "wa"), "editor", new SubjectRef("group", "herd", "member")),
            new RelationTuple(new EntityRef("group", "herd"), "member", new SubjectRef("user", "alice")),
        ]);

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());

        var check = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("asset", "ka"), "edit", new SubjectRef("user", "alice"), ctx));
        check.Allowed.ShouldBeTrue();

        var list = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            t, new SubjectRef("user", "alice"), "asset", "edit", ctx));
        list.ObjectIds.OrderBy(x => x).ShouldBe(["ka", "wa"]);

        var log = await relations.ReadChangeLogAsync(t, new ChangeLogFilter());
        log.ShouldContain(e => e.Actor == "admin-a" && e.Operation == "write");
    }
}
