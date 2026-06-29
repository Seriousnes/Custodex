using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests.Di;

public class TimestampConditionFidelityTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Schema BuildSchema() => new("v1",
        [
            new EntityTypeDef("resource",
                [new RelationDef("editor", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("editor"))]),
        ],
        [
            new ConditionDef("fresh", [],
                new Compare(new ContextNow(), CompareOp.Le, new AttributeRef("expires"))),
        ]);

    [Fact]
    public async Task Timestamp_attribute_round_tripped_as_string_is_evaluated()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UseSqlite(fx.ConnectionString);
        var provider = services.BuildServiceProvider();

        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "ts-cond";
        var t = new TenantContext(store, "tenant-1");

        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);
        await tenants.CreateTenantAsync(new TenantContext(store, store));
        await schemas.SetActiveSchemaAsync(store, BuildSchema());

        await relations.WriteTuplesAsync(t, "admin",
        [
            new RelationTuple(new EntityRef("resource", "current"), "editor",
                new SubjectRef("user", "alice"), new ConditionRef("fresh", new Dictionary<string, object?>())),
            new RelationTuple(new EntityRef("resource", "lapsed"), "editor",
                new SubjectRef("user", "alice"), new ConditionRef("fresh", new Dictionary<string, object?>())),
        ]);

        await relations.WriteAttributesAsync(t, "admin",
            new EntityRef("resource", "current"),
            new Dictionary<string, object?> { ["expires"] = Now.AddDays(1) });
        await relations.WriteAttributesAsync(t, "admin",
            new EntityRef("resource", "lapsed"),
            new Dictionary<string, object?> { ["expires"] = Now.AddDays(-1) });

        var ctx = new RequestContext(Now, new SubjectRef("user", "alice"), new Dictionary<string, object?>());

        var current = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("resource", "current"), "edit", new SubjectRef("user", "alice"), ctx));
        current.Allowed.ShouldBeTrue();

        var lapsed = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("resource", "lapsed"), "edit", new SubjectRef("user", "alice"), ctx));
        lapsed.Allowed.ShouldBeFalse();

        var list = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            t, new SubjectRef("user", "alice"), "resource", "edit", ctx));
        list.ObjectIds.ShouldBe(["current"]);
    }
}
