using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.MySql.Tests.Di;

[Collection("mysql")]
public class ConditionedCheckTests(MySqlFixture fx)
{
    [Fact]
    public async Task Conditioned_check_evaluates_native_typed_params_and_attributes_through_the_engine()
    {
        var schemaBuilder = new SchemaBuilder("v1")
            .Type("asset", t => t
                .Relation("editor", s => s.User())
                .Permission("edit", p => p.Relation("editor")))
            .Condition("min_level",
                p => p.Int("threshold"),
                b => b.Ge(b.Attribute("level"), b.Param("threshold")));

        var services = new ServiceCollection();
        services.AddCustodex().UseMySql(fx.ConnectionString).UseSchema(schemaBuilder);
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
        await schemas.SetActiveSchemaAsync(store, schemaBuilder.Build());

        await relations.WriteTuplesAsync(t, "admin",
        [
            new RelationTuple(new EntityRef("asset", "high"), "editor", new SubjectRef("user", "alice"),
                new ConditionRef("min_level", new Dictionary<string, object?> { ["threshold"] = 5 })),
            new RelationTuple(new EntityRef("asset", "low"), "editor", new SubjectRef("user", "alice"),
                new ConditionRef("min_level", new Dictionary<string, object?> { ["threshold"] = 50 })),
        ]);

        await relations.WriteAttributesAsync(t, "admin", new EntityRef("asset", "high"),
            new Dictionary<string, object?> { ["level"] = 10 });
        await relations.WriteAttributesAsync(t, "admin", new EntityRef("asset", "low"),
            new Dictionary<string, object?> { ["level"] = 10 });

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"), new Dictionary<string, object?>());

        var passes = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("asset", "high"), "edit", new SubjectRef("user", "alice"), ctx));
        passes.Allowed.ShouldBeTrue();

        var fails = await authorizer.CheckAsync(new CheckRequest(
            t, new EntityRef("asset", "low"), "edit", new SubjectRef("user", "alice"), ctx));
        fails.Allowed.ShouldBeFalse();
    }
}
