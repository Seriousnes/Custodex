namespace Custodex.Studio.Components.Views;

internal sealed record DashboardCard(string Key, string Title, string Href);

internal static class DashboardCards
{
    public static IReadOnlyList<DashboardCard> All { get; } =
    [
        new("metrics", "Metrics", "studio/metrics"),
        new("views", "Saved views", "studio/views"),
        new("playground", "Check playground", "studio/playground"),
        new("tuples", "Tuple explorer", "studio/tuples"),
        new("schema", "Schema graph", "studio/schema"),
        new("changelog", "Change log", "studio/changelog"),
    ];

    private static readonly Dictionary<string, DashboardCard> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static DashboardCard? For(string key) => ByKey.GetValueOrDefault(key);
}
