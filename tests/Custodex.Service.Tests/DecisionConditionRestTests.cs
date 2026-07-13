using System.Net.Http.Json;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Service.Rest;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class DecisionConditionRestTests(PostgresFixture pg)
{
    private const string ObjectType = "widget";
    private const string AttributeRelation = "viewer";
    private const string AttributePermission = "view";
    private const string ParameterRelation = "editor";
    private const string ParameterPermission = "edit";
    private const string AttributeGate = "gate-attr";
    private const string ParameterGate = "gate-param";
    private const string AmountField = "amount";
    private const string EnabledParam = "enabled";
    private const long Threshold = 5;

    private WebApplicationFactory<Program> CreateFactory(string tenant) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey(tenant);
        });

    private static HttpClient CreateTenantClient(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", tenant);
        return client;
    }

    private static CheckRequestDto CheckFor(
        string tenant, string objId, string permission, Dictionary<string, object?>? attributes) =>
        new(
            Store: TestAuthHelper.AdminStore,
            Tenant: tenant,
            Object: new EntityRefDto(ObjectType, objId),
            Permission: permission,
            Context: new RequestContextDto(
                Subject: new SubjectRefDto("user", "u-1", null),
                Now: null,
                Attributes: attributes));

    private static async Task<CheckResponseDto> Check(HttpClient client, CheckRequestDto req)
    {
        var resp = await client.PostAsJsonAsync("/api/check", req);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CheckResponseDto>())!;
    }

    [Fact]
    public async Task Rest_supplied_attribute_that_satisfies_the_condition_is_allowed()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await Setup(factory, tenant);
        var client = CreateTenantClient(factory, tenant);

        var result = await Check(client, CheckFor(tenant, objId, AttributePermission,
            new Dictionary<string, object?> { [AmountField] = 10 }));

        result.Allowed.ShouldBeTrue();
        result.Decision.ShouldBe("allow");
    }

    [Fact]
    public async Task Rest_supplied_attribute_that_fails_the_condition_is_denied()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await Setup(factory, tenant);
        var client = CreateTenantClient(factory, tenant);

        var result = await Check(client, CheckFor(tenant, objId, AttributePermission,
            new Dictionary<string, object?> { [AmountField] = 1 }));

        result.Allowed.ShouldBeFalse();
        result.Decision.ShouldBe("deny");
    }

    [Fact]
    public async Task Missing_required_attribute_is_denied_fail_closed()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await Setup(factory, tenant);
        var client = CreateTenantClient(factory, tenant);

        var result = await Check(client, CheckFor(tenant, objId, AttributePermission,
            new Dictionary<string, object?>()));

        result.Allowed.ShouldBeFalse();
        result.Decision.ShouldBe("conditional");
    }

    [Fact]
    public async Task Rest_supplied_attribute_against_a_tuple_bound_parameter_is_allowed_then_denied()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(tenant);
        var objId = await Setup(factory, tenant);
        var client = CreateTenantClient(factory, tenant);

        var allowed = await Check(client, CheckFor(tenant, objId, ParameterPermission,
            new Dictionary<string, object?> { [AmountField] = 10 }));
        allowed.Allowed.ShouldBeTrue();
        allowed.Decision.ShouldBe("allow");

        var denied = await Check(client, CheckFor(tenant, objId, ParameterPermission,
            new Dictionary<string, object?> { [AmountField] = 2 }));
        denied.Allowed.ShouldBeFalse();
        denied.Decision.ShouldBe("deny");
    }

    private static async Task<string> Setup(WebApplicationFactory<Program> factory, string tenant)
    {
        var objId = $"obj-{Guid.NewGuid():N}";
        var tc = new TenantContext(TestAuthHelper.AdminStore, tenant);

        var schema = new SchemaBuilder("v1")
            .Type(ObjectType, t => t
                .Relation(AttributeRelation, s => s.Type("user"))
                .Relation(ParameterRelation, s => s.Type("user"))
                .Permission(AttributePermission, p => p.Relation(AttributeRelation).Conditioned(AttributeGate))
                .Permission(ParameterPermission, p => p.Relation(ParameterRelation)))
            .Condition(AttributeGate, _ => { },
                b => b.Ge(b.Attribute(AmountField), b.Const(Threshold)))
            .Condition(ParameterGate, p => p.Bool(EnabledParam),
                b => b.And(
                    b.Eq(b.Param(EnabledParam), b.Const(true)),
                    b.Ge(b.Attribute(AmountField), b.Const(Threshold))))
            .Build();

        using var scope = factory.Services.CreateScope();
        var storeMgr = scope.ServiceProvider.GetRequiredService<IStoreManager>();
        var tenantMgr = scope.ServiceProvider.GetRequiredService<ITenantManager>();
        var schemaMgr = scope.ServiceProvider.GetRequiredService<ISchemaManager>();
        var relMgr = scope.ServiceProvider.GetRequiredService<IRelationManager>();

        await storeMgr.CreateStoreAsync(TestAuthHelper.AdminStore);
        await tenantMgr.CreateTenantAsync(tc);
        await schemaMgr.SetActiveSchemaAsync(TestAuthHelper.AdminStore, schema);
        await relMgr.WriteTuplesAsync(tc, "test",
        [
            new RelationTuple(new EntityRef(ObjectType, objId), AttributeRelation, new SubjectRef("user", "u-1")),
            new RelationTuple(
                new EntityRef(ObjectType, objId), ParameterRelation, new SubjectRef("user", "u-1"),
                new ConditionRef(ParameterGate, new Dictionary<string, object?> { [EnabledParam] = true })),
        ]);

        return objId;
    }
}
