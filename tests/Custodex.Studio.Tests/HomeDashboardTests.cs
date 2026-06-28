using Bunit;

using Custodex.Studio;
using Custodex.Studio.Components.Pages;
using Custodex.Studio.Views;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class HomeDashboardTests
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

    private static StudioView Layout(TestWorld world, string key, DashboardLayout layout, int day = 1) =>
        new(world.Tenant.Store, world.Tenant.Tenant, StudioView.SharedOwner, DashboardLayout.Kind,
            key, layout.ToJson(), DateTimeOffset.UnixEpoch.AddDays(day));

    [Fact]
    public void Disconnected_renders_all_cards_and_no_editor()
    {
        using var ctx = CreateContext(new StudioConnectionState(), new FakeStudioViewStore());

        var cut = ctx.Render<Home>();

        cut.FindAll(".dashboard-card").Count.ShouldBe(6);
        cut.FindAll(".layout-editor").ShouldBeEmpty();
        cut.FindAll("#save-layout").ShouldBeEmpty();
    }

    [Fact]
    public void Toggling_a_card_off_hides_it_from_the_dashboard()
    {
        var world = TestWorld.New();
        var hidden = DashboardLayout.AllCards[0];
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, new FakeStudioViewStore());
        var cut = ctx.Render<Home>();

        cut.Find($"#toggle-{hidden}").Change(false);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".dashboard-card").Count.ShouldBe(5);
            cut.FindAll($"#card-{hidden}").ShouldBeEmpty();
        });
    }

    [Fact]
    public void Reordering_moves_a_card_in_the_dashboard()
    {
        var world = TestWorld.New();
        var first = DashboardLayout.AllCards[0];
        var second = DashboardLayout.AllCards[1];
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, new FakeStudioViewStore());
        var cut = ctx.Render<Home>();

        cut.Find($"#down-{first}").Click();

        cut.WaitForAssertion(() => cut.FindAll(".dashboard-card")[0].Id.ShouldBe($"card-{second}"));
    }

    [Fact]
    public void Saving_persists_a_shared_layout_view_that_round_trips()
    {
        var world = TestWorld.New();
        var key = world.ObjectId();
        var store = new FakeStudioViewStore();
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, store);
        var cut = ctx.Render<Home>();

        cut.Find("#layout-name").Change(key);
        cut.Find("#save-layout").Click();

        cut.WaitForAssertion(() =>
        {
            store.LastSaved.ShouldNotBeNull();
            var saved = store.LastSaved!;
            saved.Store.ShouldBe(world.Tenant.Store);
            saved.Tenant.ShouldBe(world.Tenant.Tenant);
            saved.Owner.ShouldBe(StudioView.SharedOwner);
            saved.Kind.ShouldBe(DashboardLayout.Kind);
            saved.Key.ShouldBe(key);
            DashboardLayout.FromJson(saved.ConfigJson).VisibleCardsInOrder.ShouldBe(DashboardLayout.AllCards);
        });
    }

    [Fact]
    public void Loading_a_stored_layout_applies_it_to_the_dashboard()
    {
        var world = TestWorld.New();
        var key = world.ObjectId();
        var schema = DashboardLayout.AllCards[4];
        var metrics = DashboardLayout.AllCards[0];
        var store = new FakeStudioViewStore();
        store.Seed(Layout(world, key, new DashboardLayout([schema, metrics])));
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, store);
        var cut = ctx.Render<Home>();

        cut.WaitForElement(".load-layout").Click();

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll(".dashboard-card");
            cards.Count.ShouldBe(2);
            cards[0].Id.ShouldBe($"card-{schema}");
            cards[1].Id.ShouldBe($"card-{metrics}");
        });
    }

    [Fact]
    public void Deleting_a_saved_layout_calls_the_store_with_the_full_key()
    {
        var world = TestWorld.New();
        var key = world.ObjectId();
        var store = new FakeStudioViewStore();
        store.Seed(Layout(world, key, DashboardLayout.Default));
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, store);
        var cut = ctx.Render<Home>();

        cut.WaitForElement(".delete-layout").Click();

        cut.WaitForAssertion(() =>
        {
            store.LastDelete.ShouldNotBeNull();
            store.LastDelete!.Value.ShouldBe(
                (world.Tenant.Store, world.Tenant.Tenant, StudioView.SharedOwner, DashboardLayout.Kind, key));
        });
    }
}
