using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests.Di;

public class EndToEndTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    [Fact]
    public async Task Define_schema_write_tuples_then_check_list_objects_and_list_subjects()
    {
        var schemaBuilder = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("asset", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")));

        var services = new ServiceCollection();
        services.AddCustodex().UseSqlite(fx.ConnectionString).UseSchema(schemaBuilder);
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

        var allowed = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("asset", "ka"), "edit", new SubjectRef("user", "alice"), ctx));
        allowed.Allowed.ShouldBeTrue();

        var denied = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("asset", "ka"), "edit", new SubjectRef("user", "bob"), ctx));
        denied.Allowed.ShouldBeFalse();

        var list = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            t, new SubjectRef("user", "alice"), "asset", "edit", ctx));
        list.ObjectIds.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["ka", "wa"]);

        var subjects = await authorizer.ListSubjectsAsync(new ListSubjectsRequest(
            t, new EntityRef("asset", "ka"), "edit", ctx));
        subjects.Subjects.ShouldContain(s => s.Type == "user" && s.Id == "alice");

        var log = await relations.ReadChangeLogAsync(t, new ChangeLogFilter());
        log.ShouldContain(e => e.Actor == "admin-a" && e.Operation == "write");
    }
}
