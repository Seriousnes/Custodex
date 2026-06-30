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
    public void Resource_entity_ref_is_used_directly()
    {
        var world = TestWorld.New();
        var entity = new EntityRef(world.EntityType(), world.ObjectId());

        new ResourceEntityRefResolver().TryResolve(Context(world.EntityType(), resource: entity), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(entity);
    }

    [Fact]
    public void Resource_string_combines_with_policy_type()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();

        new ResourceIdResolver().TryResolve(Context(type, resource: id), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public void Empty_resource_string_does_not_resolve()
    {
        new ResourceIdResolver().TryResolve(Context("thing", resource: ""), out _).ShouldBeFalse();
    }

    [Fact]
    public void Route_value_binds_by_id_key()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues["id"] = id;

        new RouteValueResolver().TryResolve(Context(type, http: http), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public void Route_value_binds_by_type_id_key()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues[type + "Id"] = id;

        new RouteValueResolver().TryResolve(Context(type, http: http), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public void Route_value_binding_metadata_overrides_key_and_type()
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

        new RouteValueResolver().TryResolve(Context(policyType, http: http), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(boundType, id));
    }

    [Fact]
    public void No_http_context_does_not_resolve_from_route()
    {
        new RouteValueResolver().TryResolve(Context("thing"), out _).ShouldBeFalse();
    }

    [Fact]
    public void Root_object_resolves_from_options_delegate()
    {
        var world = TestWorld.New();
        var root = new EntityRef(world.EntityType(), world.ObjectId());
        var resolver = new RootObjectResolver(Options.Create(new CustodexAuthorizationOptions { RootObject = _ => root }));

        resolver.TryResolve(Context("thing"), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(root);
    }

    [Fact]
    public void Root_object_absent_does_not_resolve()
    {
        var resolver = new RootObjectResolver(Options.Create(new CustodexAuthorizationOptions()));

        resolver.TryResolve(Context("thing"), out _).ShouldBeFalse();
    }
}
