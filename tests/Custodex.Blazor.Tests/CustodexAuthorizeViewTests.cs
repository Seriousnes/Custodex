using System.Security.Claims;

using Bunit;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Blazor;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Blazor.Tests;

public class CustodexAuthorizeViewTests
{
    private sealed class Scenario(
        BunitContext context, TestWorld world, RecordingAuthorizer authorizer,
        string objType, string objId, string permission, string subjectId) : IDisposable
    {
        public BunitContext Context { get; } = context;
        public TestWorld World { get; } = world;
        public RecordingAuthorizer Authorizer { get; } = authorizer;
        public string ObjType { get; } = objType;
        public string ObjId { get; } = objId;
        public string Permission { get; } = permission;
        public string SubjectId { get; } = subjectId;
        public void Dispose() => Context.Dispose();
    }

    private static Scenario Arrange(TestWorld world, CheckResult result)
    {
        var objType = world.EntityType();
        var objId = world.ObjectId();
        var permission = world.Permission();
        var subjectId = world.SubjectId();

        var ctx = new BunitContext();
        var authorizer = new RecordingAuthorizer(result);
        ctx.Services.AddSingleton<IAuthorizer>(authorizer);
        ctx.Services.AddLogging();
        ctx.Services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);
        ctx.Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();

        return new Scenario(ctx, world, authorizer, objType, objId, permission, subjectId);
    }

    private static Task<AuthenticationState> AuthState(Scenario s) =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, s.SubjectId),
            new Claim(CustodexClaimTypes.Store, s.World.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, s.World.Tenant.Tenant),
        ], "Test"))));

    private static IRenderedComponent<IComponent> RenderView(Scenario s, Task<AuthenticationState> authState) =>
        s.Context.Render(builder =>
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, nameof(CascadingValue<Task<AuthenticationState>>.Value), authState);
            builder.AddAttribute(2, nameof(CascadingValue<Task<AuthenticationState>>.IsFixed), true);
            builder.AddAttribute(3, nameof(CascadingValue<Task<AuthenticationState>>.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<CustodexAuthorizeView>(0);
                inner.AddAttribute(1, nameof(CustodexAuthorizeView.ObjectType), s.ObjType);
                inner.AddAttribute(2, nameof(CustodexAuthorizeView.ObjectId), s.ObjId);
                inner.AddAttribute(3, nameof(CustodexAuthorizeView.Permission), s.Permission);
                inner.AddAttribute(4, nameof(CustodexAuthorizeView.Authorized),
                    (RenderFragment<AuthenticationState>)(_ => mb => mb.AddMarkupContent(0, "<span id=\"ok\">granted</span>")));
                inner.AddAttribute(5, nameof(CustodexAuthorizeView.NotAuthorized),
                    (RenderFragment<AuthenticationState>)(_ => mb => mb.AddMarkupContent(0, "<span id=\"no\">denied</span>")));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    [Fact]
    public void Allowed_renders_authorized_and_checks_the_resolved_object()
    {
        using var s = Arrange(TestWorld.New(), new CheckResult(true));

        var cut = RenderView(s, AuthState(s));

        cut.WaitForAssertion(() => cut.FindAll("#ok").ShouldNotBeEmpty());
        s.Authorizer.Last.ShouldNotBeNull();
        s.Authorizer.Last!.Object.ShouldBe(new EntityRef(s.ObjType, s.ObjId));
        s.Authorizer.Last.Permission.ShouldBe(s.Permission);
        s.Authorizer.Last.Subject.Id.ShouldBe(s.SubjectId);
    }

    [Fact]
    public void Denied_renders_not_authorized()
    {
        using var s = Arrange(TestWorld.New(), new CheckResult(false));

        var cut = RenderView(s, AuthState(s));

        cut.WaitForAssertion(() => cut.FindAll("#no").ShouldNotBeEmpty());
    }
}
