namespace WorkshopSentinel.ViewModels;

/// <summary>One row in the Games tab. Computed up front when the games list is first built.</summary>
public sealed class GameRow
{
    public uint   AppId           { get; init; }
    public string Name            { get; init; } = "";
    public int    SubCount        { get; init; }
    public int    StaleCount      { get; init; }
    public string LibraryRoot     { get; init; } = "";

    public string SubCountText    => SubCount.ToString();
    public string StaleText       => StaleCount > 0 ? StaleCount.ToString() : "—";
    public string Summary         => StaleCount > 0
        ? $"{SubCount} subscribed — {StaleCount} stale"
        : $"{SubCount} subscribed — all current";
}
