using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests.Di;

public class ConditionFidelityTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private static Schema BuildSchema() => new("v1",
        [
            new EntityTypeDef("resource",
                [new RelationDef("editor", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("editor"))]),
        ],
        [
            new ConditionDef("within_limit",
                [new ConditionParam("limit", ConditionType.Int)],
                new Compare(new AttributeRef("weight"), CompareOp.Le, new ParamRef("limit"))),
        ]);

    [Fact]
    public async Task Conditioned_check_reads_attribute_and_param_as_clr_values()
    {
        var schema = BuildSchema();

        var services = new ServiceCollection();
        services.AddCustodex().UseSqlite(fx.ConnectionString);
        var provider = services.BuildServiceProvider();

        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "cond";
        var t = new TenantContext(store, "tenant-1");

        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);
        await tenants.CreateTenantAsync(new TenantContext(store, store));
        await schemas.SetActiveSchemaAsync(store, schema);

        var limit = new Dictionary<string, object?> { ["limit"] = 100 };
        await relations.WriteTuplesAsync(t, "admin",
        [
            new RelationTuple(new EntityRef("resource", "under"), "editor",
                new SubjectRef("user", "alice"), new ConditionRef("within_limit", limit)),
            new RelationTuple(new EntityRef("resource", "over"), "editor",
                new SubjectRef("user", "alice"), new ConditionRef("within_limit", limit)),
        ]);

        await relations.WriteAttributesAsync(t, "admin",
            new EntityRef("resource", "under"), new Dictionary<string, object?> { ["weight"] = 50 });
        await relations.WriteAttributesAsync(t, "admin",
            new EntityRef("resource", "over"), new Dictionary<string, object?> { ["weight"] = 150 });

        var ctx = new RequestContext(
            DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());

        var satisfied = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("resource", "under"), "edit", new SubjectRef("user", "alice"), ctx));
        satisfied.Allowed.ShouldBeTrue();

        var unsatisfied = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("resource", "over"), "edit", new SubjectRef("user", "alice"), ctx));
        unsatisfied.Allowed.ShouldBeFalse();

        var list = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            t, new SubjectRef("user", "alice"), "resource", "edit", ctx));
        list.ObjectIds.ShouldBe(["under"]);
    }
}
