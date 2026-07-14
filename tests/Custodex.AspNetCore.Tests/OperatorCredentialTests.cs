using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Grpc.Core;

using Microsoft.AspNetCore.Http;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class OperatorCredentialTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    private static async Task<TenantContextAccessor> RunMiddlewareAsync(ClaimsPrincipal principal, params (string Name, string Value)[] headers)
    {
        var http = new DefaultHttpContext { User = principal };
        foreach (var (name, value) in headers)
            http.Request.Headers[name] = value;

        var accessor = new TenantContextAccessor();
        await new TenantResolutionMiddleware(_ => Task.CompletedTask).InvokeAsync(http, accessor);
        return accessor;
    }

    [Fact]
    public async Task Default_credential_is_bound_to_its_store_and_rejects_a_foreign_store()
    {
        var world = TestWorld.New();
        var foreignStore = world.EntityType();

        var accessor = await RunMiddlewareAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)));

        accessor.IsOperator.ShouldBeFalse();
        accessor.AuthenticatedStore.ShouldBe(world.Tenant.Store);
        accessor.Current.Store.ShouldBe(world.Tenant.Store);
        ((ITenantContextAccessor)accessor).IsStoreAuthorized(world.Tenant.Store).ShouldBeTrue();
        ((ITenantContextAccessor)accessor).IsStoreAuthorized(foreignStore).ShouldBeFalse();
    }

    [Fact]
    public async Task Operator_credential_takes_the_store_from_the_per_call_header_and_authorizes_any_store()
    {
        var world = TestWorld.New();
        var boundStore = world.Tenant.Store;
        var targetStore = world.EntityType();

        var accessor = await RunMiddlewareAsync(
            Principal(
                new Claim("Custodex:store", boundStore),
                new Claim("Custodex:allowAllStores", "true"),
                new Claim("Custodex:tenant", "*")),
            (TenantResolutionMiddleware.StoreHeader, targetStore),
            (TenantResolutionMiddleware.TenantHeader, world.Tenant.Tenant));

        accessor.IsOperator.ShouldBeTrue();
        accessor.AuthenticatedStore.ShouldBe(boundStore);
        accessor.Current.Store.ShouldBe(targetStore);
        accessor.Current.Tenant.ShouldBe(world.Tenant.Tenant);
        ((ITenantContextAccessor)accessor).IsStoreAuthorized(targetStore).ShouldBeTrue();
        ((ITenantContextAccessor)accessor).IsStoreAuthorized(world.EntityType()).ShouldBeTrue();
    }

    [Fact]
    public async Task Non_operator_sending_the_store_header_has_it_ignored_and_stays_rejected()
    {
        var world = TestWorld.New();
        var foreignStore = world.EntityType();

        var accessor = await RunMiddlewareAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)),
            (TenantResolutionMiddleware.StoreHeader, foreignStore));

        accessor.IsOperator.ShouldBeFalse();
        accessor.AuthenticatedStore.ShouldBe(world.Tenant.Store);
        accessor.Current.Store.ShouldBe(world.Tenant.Store);
        ((ITenantContextAccessor)accessor).IsStoreAuthorized(foreignStore).ShouldBeFalse();
    }

    [Fact]
    public async Task Schema_service_rejects_a_foreign_store_for_a_default_credential()
    {
        var world = TestWorld.New();
        var foreignStore = world.EntityType();
        var accessor = await RunMiddlewareAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)),
            (TenantResolutionMiddleware.StoreHeader, foreignStore));

        var service = new SchemaGrpcService(new RecordingSchemaManager(), accessor);
        var request = new Custodex.Api.SetActiveSchemaRequest { Store = foreignStore, SchemaJson = "{}" };

        var ex = await Should.ThrowAsync<RpcException>(() => service.SetActive(request, null!));
        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Schema_service_lets_an_operator_credential_activate_a_schema_on_a_foreign_store()
    {
        var world = TestWorld.New();
        var targetStore = world.EntityType();
        var accessor = await RunMiddlewareAsync(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:allowAllStores", "true"),
                new Claim("Custodex:tenant", "*")),
            (TenantResolutionMiddleware.StoreHeader, targetStore),
            (TenantResolutionMiddleware.TenantHeader, world.Tenant.Tenant));

        var schema = new Custodex.Core.SchemaBuilder("1").Type(world.EntityType(), _ => { }).Build();
        var manager = new RecordingSchemaManager();
        var service = new SchemaGrpcService(manager, accessor);
        var request = new Custodex.Api.SetActiveSchemaRequest
        {
            Store = targetStore,
            SchemaJson = SchemaJson.Serialize(schema),
        };

        await service.SetActive(request, new TestServerCallContext());

        manager.LastStore.ShouldBe(targetStore);
    }

    private sealed class RecordingSchemaManager : ISchemaManager
    {
        public string? LastStore { get; private set; }

        public SchemaValidationResult ValidateSchema(Schema schema) => new(true, []);

        public Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
        {
            LastStore = store;
            return Task.CompletedTask;
        }

        public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default) => Task.FromResult<Schema?>(null);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        protected override string MethodCore => string.Empty;
        protected override string HostCore => string.Empty;
        protected override string PeerCore => string.Empty;
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
