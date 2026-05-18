using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.ViewModels;

/// <summary>One row in the per-game audit grid (My Mods tab).</summary>
public sealed class AuditedItemRow
{
    public AuditedItem Source { get; }
    public AuditedItemRow(AuditedItem source) { Source = source; }

    public ulong  PublishedFileId => Source.Local.PublishedFileId;
    public string Title           => Source.Remote?.Title ?? $"#{Source.Local.PublishedFileId}";
    public string StatusText      => Source.Status.ToString();
    public string StatusIcon      => Source.Status switch
    {
        FreshnessStatus.Current   => "✓",   // ✓
        FreshnessStatus.Stale     => "⚠",   // ⚠
        FreshnessStatus.Unknown   => "?",
        FreshnessStatus.Removed   => "✘",   // ✘
        FreshnessStatus.ApiFailed => "!",
        _ => "?"
    };

    public string LocalTime  => FormatRelative(Source.Local.LocalTimeUpdated);
    public string RemoteTime => Source.Remote?.RemoteTimeUpdated is long t ? FormatRelative(t) : "—";
    public string Delta      => Source.Status == FreshnessStatus.Stale && Source.Remote?.RemoteTimeUpdated is long rt
        ? "+" + FormatDuration(rt - Source.Local.LocalTimeUpdated)
        : "";
    public string SizeText   => Source.Local.LocalSizeBytes > 0 ? FormatSize(Source.Local.LocalSizeBytes) : "";

    // Every status is refreshable on demand. Unknown (friends-only items the API won't
    // expose) is the most important one in practice — those are where the author's own
    // unpublished/private revisions live, and the user has no other way to nudge them.
    public bool IsRefreshable => Source.Status != FreshnessStatus.Removed;

    public WorkshopItemLocal LocalItem => Source.Local;

    private static string FormatRelative(long epochSeconds)
    {
        if (epochSeconds <= 0) return "—";
        var ts = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        return FormatDuration((long)ts.TotalSeconds) + " ago";
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 0) seconds = -seconds;
        if (seconds < 60)     return $"{seconds}s";
        if (seconds < 3600)   return $"{seconds / 60}min";
        if (seconds < 86400)  return $"{seconds / 3600}h";
        if (seconds < 2592000)return $"{seconds / 86400}d";
        return $"{seconds / 2592000}mo";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)         return $"{bytes} B";
        if (bytes < 1024 * 1024)  return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>Brush converter for the status-icon column.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Brushes.Gray;
        return s switch
        {
            "Current"   => Brushes.SeaGreen,
            "Stale"     => Brushes.DarkOrange,
            "Removed"   => Brushes.IndianRed,
            "ApiFailed" => Brushes.IndianRed,
            _           => Brushes.Gray,
        };
    }
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
