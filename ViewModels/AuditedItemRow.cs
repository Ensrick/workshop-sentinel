using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.ViewModels;

/// <summary>
/// One row in the My Mods grid. Represents one Workshop mod, either:
/// <list type="bullet">
///   <item>An audit row for a mod the local user is subscribed to (<see cref="Mine"/> = true,
///   <see cref="Source"/> populated with both Local + Remote).</item>
///   <item>A friend-exclusive row for a mod some added friend has but the local user doesn't
///   (<see cref="Mine"/> = false, <see cref="LocalItem"/> = null, audit columns show "—").</item>
/// </list>
///
/// The <see cref="YouIcon"/> column is the interactive toggle: <c>✓</c> when subscribed
/// (clicking unsubscribes), <c>+</c> when not subscribed (clicking subscribes). Friend
/// columns read off the <see cref="FriendHas"/> dictionary via WPF's indexer binding syntax
/// <c>[fSTEAMID]</c>.
/// </summary>
public sealed class AuditedItemRow : INotifyPropertyChanged
{
    /// <summary>Audit data for a mod the user is subscribed to. Null for friend-exclusive rows.</summary>
    public AuditedItem? Source { get; }

    /// <summary>Always populated — even friend-exclusive rows have a remote we can title-lookup.</summary>
    private readonly WorkshopItemRemote? _remote;
    private readonly ulong _publishedFileId;

    private bool _mine;
    /// <summary>True when the local user is subscribed to this mod.</summary>
    public bool Mine
    {
        get => _mine;
        set
        {
            if (_mine == value) return;
            _mine = value;
            OnChanged(nameof(Mine));
            OnChanged(nameof(YouIcon));
            OnChanged(nameof(YouTooltip));
        }
    }

    /// <summary>friend.SteamId64 → did they subscribe to this mod?</summary>
    public Dictionary<string, bool> FriendHas { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// String-keyed indexer so the matrix can bind friend cells as <c>[fSTEAMID]</c>.
    /// Returns the symbol the cell should show. Per-cell color is decided in the DataTemplate.
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (key.StartsWith("f", StringComparison.Ordinal))
            {
                var sid = key.Substring(1);
                return FriendHas.TryGetValue(sid, out var has) && has ? "✓" : "▢";
            }
            return "";
        }
    }

    /// <summary>Audit-row constructor: a mod the user is subscribed to.</summary>
    public AuditedItemRow(AuditedItem source)
    {
        Source           = source;
        _remote          = source.Remote;
        _publishedFileId = source.Local.PublishedFileId;
        _mine            = true;
    }

    /// <summary>Friend-exclusive constructor: a mod some friend has but the user doesn't.</summary>
    public AuditedItemRow(ulong publishedFileId, WorkshopItemRemote? remote)
    {
        Source           = null;
        _remote          = remote;
        _publishedFileId = publishedFileId;
        _mine            = false;
    }

    public ulong  PublishedFileId => _publishedFileId;
    public string Title           => _remote?.Title ?? $"#{_publishedFileId}";

    public string StatusText      => Source is { } s ? s.Status.ToString() : "—";
    public string StatusIcon      => Source is { } s ? s.Status switch
    {
        FreshnessStatus.Current   => "✓",
        FreshnessStatus.Stale     => "⚠",
        FreshnessStatus.Unknown   => "?",
        FreshnessStatus.Removed   => "✘",
        FreshnessStatus.ApiFailed => "!",
        _ => "?"
    } : "";

    public string LocalTime  => Source is { } s ? FormatRelative(s.Local.LocalTimeUpdated) : "—";
    public string RemoteTime => _remote?.RemoteTimeUpdated is long t ? FormatRelative(t) : "—";
    public string Delta      => Source is { Status: FreshnessStatus.Stale, Remote.RemoteTimeUpdated: long rt } && Source.Local.LocalTimeUpdated > 0
        ? "+" + FormatDuration(rt - Source.Local.LocalTimeUpdated)
        : "";
    public string SizeText   => Source?.Local.LocalSizeBytes > 0
        ? FormatSize(Source.Local.LocalSizeBytes)
        : (_remote?.RemoteSizeBytes is long rs && rs > 0 ? FormatSize(rs) : "");

    /// <summary>Refresh button only makes sense for mods the user is subscribed to.</summary>
    public bool IsRefreshable => Mine && Source is { Status: not FreshnessStatus.Removed };

    /// <summary>You-column toggle icon: ✓ when subscribed, + when not.</summary>
    public string YouIcon    => Mine ? "✓" : "+";
    public string YouTooltip => Mine ? "Subscribed. Click to unsubscribe." : "Not subscribed. Click to subscribe.";

    public WorkshopItemLocal? LocalItem => Source?.Local;
    public WorkshopItemRemote? RemoteItem => _remote;

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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Update which friends subscribe to this mod and emit a single change notification
    /// for every cell binding. Used after a friend is added/removed so the matrix re-renders.
    /// </summary>
    public void SetFriendHas(string steamId64, bool has)
    {
        FriendHas[steamId64] = has;
        OnChanged($"Item[]");   // bumps every indexer binding
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
