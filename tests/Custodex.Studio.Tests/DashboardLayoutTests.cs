using Custodex.Studio.Views;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class DashboardLayoutTests
{
    [Fact]
    public void Default_shows_every_card_in_natural_order()
    {
        DashboardLayout.Default.VisibleCardsInOrder.ShouldBe(DashboardLayout.AllCards);
        DashboardLayout.AllCards.Count.ShouldBe(6);
    }

    [Fact]
    public void Round_trips_through_config_json()
    {
        var layout = new DashboardLayout([DashboardLayout.AllCards[3], DashboardLayout.AllCards[0]]);

        var restored = DashboardLayout.FromJson(layout.ToJson());

        restored.VisibleCardsInOrder.ShouldBe(layout.VisibleCardsInOrder);
    }

    [Fact]
    public void From_json_falls_back_to_default_when_empty()
    {
        DashboardLayout.FromJson(string.Empty).VisibleCardsInOrder.ShouldBe(DashboardLayout.AllCards);
    }
}
