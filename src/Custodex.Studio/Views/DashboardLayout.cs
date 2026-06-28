using System.Text.Json;

namespace Custodex.Studio.Views;

/// <summary>
/// The dashboard widget cards an operator has chosen to display, in display order. Cards omitted
/// from <see cref="VisibleCardsInOrder"/> are hidden. Serializes to and from
/// <see cref="StudioView.ConfigJson"/> for persistence.
/// </summary>
/// <param name="VisibleCardsInOrder">The widget-area keys to display, in the order to display them.</param>
public sealed record DashboardLayout(IReadOnlyList<string> VisibleCardsInOrder)
{
    /// <summary>The <see cref="StudioView.Kind"/> value categorizing persisted dashboard layouts.</summary>
    public const string Kind = "layout";

    /// <summary>Every dashboard widget-area key, in the natural order cards appear by default.</summary>
    public static IReadOnlyList<string> AllCards { get; } =
        ["metrics", "views", "playground", "tuples", "schema", "changelog"];

    /// <summary>A layout that shows every card in its natural order.</summary>
    public static DashboardLayout Default { get; } = new(AllCards);

    /// <summary>Serializes this layout to the JSON stored in <see cref="StudioView.ConfigJson"/>.</summary>
    /// <returns>The layout as a JSON string.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reconstructs a layout from JSON produced by <see cref="ToJson"/>.</summary>
    /// <param name="json">The JSON to read.</param>
    /// <returns>The deserialized layout, or <see cref="Default"/> when the JSON is empty.</returns>
    public static DashboardLayout FromJson(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? Default
            : JsonSerializer.Deserialize<DashboardLayout>(json, Options) ?? Default;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
