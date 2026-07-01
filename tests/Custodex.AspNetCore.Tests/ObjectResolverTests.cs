using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class ObjectResolverTests
{
    private static CustodexResolutionContext Context(string objectType, object? resource = null, HttpContext? http = null) =>
        ResolutionContextFactory.Create(objectType, "view", resource, http, new TenantContext("s", "t"));

    [Fact]
    public async Task Resource_entity_ref_is_used_directly()
    {
        var world = TestWorld.New();
        var entity = new EntityRef(world.EntityType(), world.ObjectId());

        var resolved = await new ResourceEntityRefResolver().ResolveAsync(Context(world.EntityType(), resource: entity), CancellationToken.None);

        resolved.ShouldBe(entity);
    }

    [Fact]
    public async Task Resource_string_combines_with_policy_type()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();

        var resolved = await new ResourceIdResolver().ResolveAsync(Context(type, resource: id), CancellationToken.None);

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public async Task Empty_resource_string_does_not_resolve()
    {
        (await new ResourceIdResolver().ResolveAsync(Context("thing", resource: ""), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Route_value_binds_by_id_key()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues["id"] = id;

        var resolved = await new RouteValueResolver().ResolveAsync(Context(type, http: http), CancellationToken.None);

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public async Task Route_value_binds_by_type_id_key()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues[type + "Id"] = id;

        var resolved = await new RouteValueResolver().ResolveAsync(Context(type, http: http), CancellationToken.None);

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public async Task Route_value_binding_metadata_overrides_key_and_type()
    {
        var world = TestWorld.New();
        var policyType = world.EntityType();
        var boundType = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues["slug"] = id;
        http.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new CustodexObjectBindingMetadata("slug", boundType)),
            "test"));

        var resolved = await new RouteValueResolver().ResolveAsync(Context(policyType, http: http), CancellationToken.None);

        resolved.ShouldBe(new EntityRef(boundType, id));
    }

    [Fact]
    public async Task No_http_context_does_not_resolve_from_route()
    {
        (await new RouteValueResolver().ResolveAsync(Context("thing"), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Root_object_resolves_from_options_delegate()
    {
        var world = TestWorld.New();
        var root = new EntityRef(world.EntityType(), world.ObjectId());
        var resolver = new RootObjectResolver(Options.Create(new CustodexAuthorizationOptions { RootObject = _ => root }));

        var resolved = await resolver.ResolveAsync(Context("thing"), CancellationToken.None);

        resolved.ShouldBe(root);
    }

    [Fact]
    public async Task Root_object_absent_does_not_resolve()
    {
        var resolver = new RootObjectResolver(Options.Create(new CustodexAuthorizationOptions()));

        (await resolver.ResolveAsync(Context("thing"), CancellationToken.None)).ShouldBeNull();
    }
}
