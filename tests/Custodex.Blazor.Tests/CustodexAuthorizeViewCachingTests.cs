using System.Security.Claims;

using Bunit;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Blazor;
using Custodex.Core.Caching;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Blazor.Tests;

public class CustodexAuthorizeViewCachingTests
{
    [Fact]
    public async Task Repeated_render_passes_reuse_one_engine_check_in_a_scope()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var objId = world.ObjectId();
        var permission = world.Permission();
        var subjectId = world.SubjectId();

        var authorizer = new CountingAuthorizer(allowed: true);
        var schemaStore = new InMemorySchemaStore();
        await schemaStore.SetActiveAsync(world.Tenant.Store, new Schema("v1", [], []), new NoOpUnitOfWork());

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IAuthorizer>(authorizer);
        ctx.Services.AddSingleton<ISchemaStore>(schemaStore);
        ctx.Services.AddSingleton<ICacheStore>(new InMemoryCacheStore());
        ctx.Services.AddLogging();
        ctx.Services.AddCustodexDecisionCache();
        ctx.Services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);
        ctx.Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();

        var authState = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subjectId),
            new Claim(CustodexClaimTypes.Store, world.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, world.Tenant.Tenant),
        ], "Test"))));

        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddAttribute(1, nameof(CascadingValue<Task<AuthenticationState>>.Value), authState);
            builder.AddAttribute(2, nameof(CascadingValue<Task<AuthenticationState>>.IsFixed), true);
            builder.AddAttribute(3, nameof(CascadingValue<Task<AuthenticationState>>.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<CustodexAuthorizeView>(0);
                inner.AddAttribute(1, nameof(CustodexAuthorizeView.ObjectType), objType);
                inner.AddAttribute(2, nameof(CustodexAuthorizeView.ObjectId), objId);
                inner.AddAttribute(3, nameof(CustodexAuthorizeView.Permission), permission);
                inner.AddAttribute(4, nameof(CustodexAuthorizeView.Authorized),
                    (RenderFragment<AuthenticationState>)(_ => mb => mb.AddMarkupContent(0, "<span id=\"ok\">granted</span>")));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => cut.FindAll("#ok").ShouldNotBeEmpty());

        for (var i = 0; i < 5; i++)
            cut.Render();

        cut.WaitForAssertion(() => cut.FindAll("#ok").ShouldNotBeEmpty());
        authorizer.CheckCalls.ShouldBe(1);
    }
}
