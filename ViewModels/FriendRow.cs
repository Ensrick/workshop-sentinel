using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.ViewModels;

/// <summary>
/// One row in the Compare-to-Friends left-panel ListBox. Wraps a SteamFriend and exposes
/// the bound display fields. Property changes trigger UI re-render — and (via the parent
/// MainWindow) re-sort + settings persistence.
/// </summary>
public sealed class FriendRow : INotifyPropertyChanged
{
    public SteamFriend Friend { get; }
    public FriendRow(SteamFriend friend) { Friend = friend; }

    public string SteamId64   => Friend.SteamId64;
    public string PersonaName => Friend.PersonaName;

    public bool IsFavorite
    {
        get => Friend.IsFavorite;
        set
        {
            if (Friend.IsFavorite == value) return;
            Friend.IsFavorite = value;
            Raise(nameof(IsFavorite));
            Raise(nameof(FavoriteIcon));
        }
    }
    public string FavoriteIcon => Friend.IsFavorite ? "★" : "☆";

    public FriendOnlineState Online
    {
        get => Friend.Online;
        set
        {
            if (Friend.Online == value) return;
            Friend.Online = value;
            Raise(nameof(Online));
            Raise(nameof(OnlineIcon));
            Raise(nameof(OnlineColor));
            Raise(nameof(OnlineTooltip));
        }
    }
    public string OnlineIcon => Friend.Online switch
    {
        FriendOnlineState.InGame  => "●",
        FriendOnlineState.Online  => "●",
        FriendOnlineState.Offline => "○",
        _ => "·",
    };
    public Brush OnlineColor => Friend.Online switch
    {
        FriendOnlineState.InGame  => Brushes.LimeGreen,
        FriendOnlineState.Online  => Brushes.DeepSkyBlue,
        FriendOnlineState.Offline => Brushes.Gray,
        _ => Brushes.DimGray,
    };
    public string OnlineTooltip => Friend.Online switch
    {
        FriendOnlineState.InGame  => "In game",
        FriendOnlineState.Online  => "Online",
        FriendOnlineState.Offline => "Offline",
        _ => "Online status not loaded — click 'Refresh online'",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>
    /// Sort key tuple — ascending: favorites first (0), then by online-state rank
    /// (InGame &lt; Online &lt; Unknown &lt; Offline), then alphabetical persona name.
    /// </summary>
    public (int FavRank, int OnlineRank, string Name) SortKey => (
        IsFavorite ? 0 : 1,
        Online switch
        {
            FriendOnlineState.InGame  => 0,
            FriendOnlineState.Online  => 1,
            FriendOnlineState.Unknown => 2,
            FriendOnlineState.Offline => 3,
            _ => 4,
        },
        PersonaName ?? "");
}
