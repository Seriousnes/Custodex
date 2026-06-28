using Bunit;

using Custodex.Studio;
using Custodex.Studio.Components.Layout;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class StudioShellTests
{
    private static BunitContext CreateContext(StudioConnectionState state)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(state);
        return ctx;
    }

    [Fact]
    public void Nav_menu_renders_a_link_for_each_widget_area()
    {
        using var ctx = CreateContext(new StudioConnectionState());

        var cut = ctx.Render<NavMenu>();

        var links = cut.FindAll("a.studio-nav-link");
        links.Count.ShouldBe(7);
        cut.Markup.ShouldContain("Overview");
        cut.Markup.ShouldContain("Metrics");
        cut.Markup.ShouldContain("Saved views");
        cut.Markup.ShouldContain("Check playground");
        cut.Markup.ShouldContain("Tuple explorer");
        cut.Markup.ShouldContain("Schema graph");
        cut.Markup.ShouldContain("Change log");
    }

    [Fact]
    public void Connection_status_starts_empty_and_reflects_the_selected_scope()
    {
        var world = TestWorld.New();
        var state = new StudioConnectionState();
        using var ctx = CreateContext(state);

        var cut = ctx.Render<ConnectionStatus>();

        cut.Markup.ShouldNotContain(world.Tenant.Store);
        state.IsConnected.ShouldBeFalse();

        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain(world.Tenant.Store);
            cut.Markup.ShouldContain(world.Tenant.Tenant);
        });
    }
}
