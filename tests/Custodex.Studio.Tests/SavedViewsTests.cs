using Bunit;

using Custodex.Studio;
using Custodex.Studio.Components.Pages;
using Custodex.Studio.Views;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class SavedViewsTests
{
    private static BunitContext CreateContext(StudioConnectionState state, FakeStudioViewStore store)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton<IStudioViewStore>(store);
        ctx.Services.AddSingleton(TimeProvider.System);
        return ctx;
    }

    [Fact]
    public void Disconnected_shows_notice_and_no_editor()
    {
        using var ctx = CreateContext(new StudioConnectionState(), new FakeStudioViewStore());

        var cut = ctx.Render<SavedViews>();

        cut.Markup.ShouldContain("Overview");
        cut.FindAll(".layout-editor").ShouldBeEmpty();
    }

    [Fact]
    public void Connected_lists_saved_layouts_for_the_scope()
    {
        var world = TestWorld.New();
        var key = world.ObjectId();
        var store = new FakeStudioViewStore();
        store.Seed(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, StudioView.SharedOwner, DashboardLayout.Kind,
            key, DashboardLayout.Default.ToJson(), DateTimeOffset.UnixEpoch));
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, store);
        var cut = ctx.Render<SavedViews>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".layout-editor").Count.ShouldBe(1);
            cut.WaitForElement(".saved-layout-row").GetAttribute("data-key").ShouldBe(key);
        });
    }
}
