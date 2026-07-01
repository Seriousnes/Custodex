using System.Net;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Core;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Shouldly;

namespace Custodex.AspNetCore.Tests.Integration;

public class CustodexAuthorizationEndpointTests
{
    private sealed class Fixture(IHost host, TestWorld world, string objType, string permission, string grantedObjectId, string grantedSubjectId) : IDisposable
    {
        public IHost Host { get; } = host;
        public TestWorld World { get; } = world;
        public string ObjType { get; } = objType;
        public string Permission { get; } = permission;
        public string GrantedObjectId { get; } = grantedObjectId;
        public string GrantedSubjectId { get; } = grantedSubjectId;
        public void Dispose() => Host.Dispose();
    }

    private static async Task<Fixture> StartAsync()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var view = world.Permission();
        var grantedObjectId = world.ObjectId();
        var grantedSubjectId = world.SubjectId();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(view, s => s.Type(world.UserType))
                .Permission(view, p => p.Relation(view)))
            .Build();

        IAuthorizer authorizer = await world.BuildAsync(schema,
            TestWorld.Tuple(objType, grantedObjectId, view, world.User(grantedSubjectId)));

        var policy = $"custodex:{objType}:{view}";
        var anyPolicy = $"custodex:any:{objType}:{view}";

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton(authorizer);
                    services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/things/{id}", () => Results.Ok()).RequireAuthorization(policy);
                        endpoints.MapGet("/widgets/{slug}", () => Results.Ok())
                            .RequireAuthorization(policy)
                            .WithCustodexObject(routeKey: "slug", type: objType);
                        endpoints.MapGet("/any-things", () => Results.Ok()).RequireAuthorization(anyPolicy);
                    });
                }))
            .StartAsync();

        return new Fixture(host, world, objType, view, grantedObjectId, grantedSubjectId);
    }

    private static HttpRequestMessage Request(string path, string? subject, TestWorld world)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (subject is not null)
        {
            request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
            request.Headers.Add(TestAuthHandler.StoreHeader, world.Tenant.Store);
            request.Headers.Add(TestAuthHandler.TenantHeader, world.Tenant.Tenant);
        }
        return request;
    }

    [Fact]
    public async Task Granted_subject_on_granted_object_gets_200()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.GrantedObjectId}", f.GrantedSubjectId, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Granted_subject_on_other_object_gets_403()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.World.ObjectId()}", f.GrantedSubjectId, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Other_subject_gets_403()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.GrantedObjectId}", f.World.SubjectId(), f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_request_gets_401()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.GrantedObjectId}", subject: null, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Any_object_granted_subject_gets_200()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request("/any-things", f.GrantedSubjectId, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Any_object_ungranted_subject_gets_403()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request("/any-things", f.World.SubjectId(), f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Any_object_anonymous_request_gets_401()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request("/any-things", subject: null, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Custom_route_key_via_metadata_binds_the_object()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var ok = await client.SendAsync(Request($"/widgets/{f.GrantedObjectId}", f.GrantedSubjectId, f.World));
        var denied = await client.SendAsync(Request($"/widgets/{f.World.ObjectId()}", f.GrantedSubjectId, f.World));

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
