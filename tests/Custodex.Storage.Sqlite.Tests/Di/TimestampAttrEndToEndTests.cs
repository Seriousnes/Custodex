using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Sqlite.Tests.Di;

public class TimestampAttrEndToEndTests(SqliteFixture fx) : IClassFixture<SqliteFixture>
{
    private static readonly DateTimeOffset InstantUtc = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SameInstantOffset = new(2024, 1, 1, 1, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset LaterInstant = new(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static Schema BuildSchema() => new("v1",
        [
            new EntityTypeDef("doc",
                [new RelationDef("editor", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("editor"))]),
        ],
        [
            new ConditionDef("match", [],
                new Compare(
                    new AttributeRef("a", ConditionType.Timestamp),
                    CompareOp.Eq,
                    new AttributeRef("b", ConditionType.Timestamp))),
        ]);

    [Fact]
    public async Task Declared_timestamp_attributes_compare_chronologically_through_the_store()
    {
        var services = new ServiceCollection();
        services.AddCustodex().UseSqlite(fx.ConnectionString);
        var provider = services.BuildServiceProvider();

        var stores = provider.GetRequiredService<IStoreManager>();
        var tenants = provider.GetRequiredService<ITenantManager>();
        var schemas = provider.GetRequiredService<ISchemaManager>();
        var relations = provider.GetRequiredService<IRelationManager>();
        var authorizer = provider.GetRequiredService<IAuthorizer>();

        const string store = "ts-attr";
        var t = new TenantContext(store, "tenant-1");

        await stores.CreateStoreAsync(store);
        await tenants.CreateTenantAsync(t);
        await tenants.CreateTenantAsync(new TenantContext(store, store));
        await schemas.SetActiveSchemaAsync(store, BuildSchema());

        var subject = new SubjectRef("user", "alice");
        await relations.WriteTuplesAsync(t, "admin",
        [
            new RelationTuple(new EntityRef("doc", "same"), "editor", subject,
                new ConditionRef("match", new Dictionary<string, object?>())),
            new RelationTuple(new EntityRef("doc", "different"), "editor", subject,
                new ConditionRef("match", new Dictionary<string, object?>())),
        ]);

        await relations.WriteAttributesAsync(t, "admin", new EntityRef("doc", "same"),
            new Dictionary<string, object?> { ["a"] = InstantUtc, ["b"] = SameInstantOffset });
        await relations.WriteAttributesAsync(t, "admin", new EntityRef("doc", "different"),
            new Dictionary<string, object?> { ["a"] = InstantUtc, ["b"] = LaterInstant });

        var ctx = new RequestContext(InstantUtc, subject, new Dictionary<string, object?>());

        var same = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("doc", "same"), "edit", subject, ctx));
        same.Allowed.ShouldBeTrue();

        var different = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("doc", "different"), "edit", subject, ctx));
        different.Allowed.ShouldBeFalse();

        var list = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            t, subject, "doc", "edit", ctx));
        list.ObjectIds.ShouldBe(["same"]);
    }
}
