using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WorkshopSentinel.Services;
using WorkshopSentinel.ViewModels;

namespace WorkshopSentinel;

public partial class MainWindow : Window
{
    // ---- services (composed once at startup) ----
    private readonly SettingsStore                 _settingsStore;
    private readonly SteamPathResolver             _pathResolver;
    private readonly LibraryFoldersResolver        _libResolver;
    private readonly WorkshopEnumerator            _enumerator = new();
    private readonly HttpClient                    _http;
    private readonly SteamWebApiClient             _api;
    private readonly FriendSubscriptionsClient     _friendClient;
    private readonly SteamProcessGuard             _steamGuard = new();
    private readonly string                        _steamRoot;
    private readonly IReadOnlyList<string>         _libraryRoots;
    private readonly AppNameResolver               _names;
    private readonly RefreshExecutor               _refresher;
    private readonly SteamFriendsResolver?         _friendsResolver;
    /// <summary>Lazy-initialized on first Subscribe click: one cookie read covers the session.</summary>
    private SteamSubscribeClient?                  _subscribeClient;
    private readonly UpdateChecker                 _updateChecker;
    private readonly UpdateInstaller               _updateInstaller;
    private UpdateCheckResult?                     _pendingUpdate;
    private bool                                   _updateBannerDismissed;

    // ---- in-memory state ----
    private readonly ObservableCollection<GameRow>         _games     = new();
    private readonly ObservableCollection<AuditedItemRow>  _myMods    = new();
    private List<WorkshopItemLocal>                        _myModsRaw = new();
    private readonly List<FriendIdentity>                  _friends   = new();
    /// <summary>friend.SteamId64 → set of subscribed publishedfileids</summary>
    private readonly Dictionary<string, HashSet<ulong>>    _friendSubs = new();
    /// <summary>Steam friend roster (read from localconfig.vdf) for the picker panel.</summary>
    private readonly ObservableCollection<FriendRow>       _friendRoster = new();
    private Settings                                       _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{Program.Version}";

        _settingsStore = new SettingsStore();
        _settings      = _settingsStore.Load();
        _pathResolver  = new SteamPathResolver(_settings);
        _libResolver   = new LibraryFoldersResolver();

        try { _steamRoot = _pathResolver.Resolve(); }
        catch (SteamNotFoundException ex)
        {
            MessageBox.Show(this,
                "Couldn't find Steam on this machine.\n\n" + ex.Message +
                "\n\nSet steamRoot in %APPDATA%\\WorkshopSentinel\\settings.json and restart.",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Error);
            _steamRoot = "";
        }

        _libraryRoots = _libResolver.Resolve(_steamRoot);
        _names        = new AppNameResolver(_libraryRoots);

        _http         = BuildHttpClient();
        _api          = new SteamWebApiClient(_http);
        _friendClient = new FriendSubscriptionsClient(_http);
        _refresher    = new RefreshExecutor(_libraryRoots);
        _friendsResolver = string.IsNullOrEmpty(_steamRoot) ? null : new SteamFriendsResolver(_steamRoot);
        _updateChecker   = new UpdateChecker(_http);
        _updateInstaller = new UpdateInstaller(_http);

        GamesGrid.ItemsSource    = _games;
        MyModsGrid.ItemsSource   = _myMods;
        FriendsListBox.ItemsSource = _friendRoster;

        // Initial population
        Loaded += async (_, _) =>
        {
            // Update check is fire-and-forget — failures land in the status footer, never a dialog.
            _ = CheckForUpdatesAsync();
            await ReloadGamesAsync();
            ReloadFriendRoster();
        };
    }

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"WorkshopSentinel/{Program.Version}");
        // Some Steam endpoints serve different content based on Accept-Language; pin to en-US so
        // private-profile detection strings stay predictable across user locales.
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return c;
    }

    // ===================================================================
    //  Games tab
    // ===================================================================

    private async void OnRescanGamesClicked(object sender, RoutedEventArgs e)
        => await ReloadGamesAsync();

    private async Task ReloadGamesAsync()
    {
        if (string.IsNullOrEmpty(_steamRoot)) return;

        SetStatus("Scanning local Workshop manifests…");
        _games.Clear();

        // 1. Group local items by appid.
        var byApp = await Task.Run(() =>
            _enumerator.EnumerateAll(_libraryRoots)
                .GroupBy(i => i.AppId)
                .ToDictionary(g => g.Key, g => g.ToList()));

        if (byApp.Count == 0)
        {
            SetStatus("No Workshop subscriptions found on this machine.");
            return;
        }

        // 2. Fetch live timestamps for every item (one big batched call).
        SetStatus($"Auditing {byApp.Values.Sum(v => v.Count)} items against the Steam Web API…");
        var allIds = byApp.Values.SelectMany(v => v.Select(i => i.PublishedFileId));
        var remotes = await _api.GetPublishedFileDetailsAsync(allIds);

        // 3. Compute (subs, stale) per appid.
        foreach (var (appId, items) in byApp.OrderBy(kv => _names.Resolve(kv.Key), StringComparer.OrdinalIgnoreCase))
        {
            int stale = 0;
            foreach (var item in items)
            {
                remotes.TryGetValue(item.PublishedFileId, out var r);
                if (StalenessAuditor.Audit(item, r, null).Status == FreshnessStatus.Stale) stale++;
            }
            _games.Add(new GameRow
            {
                AppId       = appId,
                Name        = _names.Resolve(appId),
                SubCount    = items.Count,
                StaleCount  = stale,
                LibraryRoot = FindLibraryFor(appId) ?? "",
            });
        }

        // Refresh the game dropdowns in the other tabs.
        var snapshot = _games.ToList();
        MyModsGameCombo.ItemsSource = snapshot;
        FriendGameCombo.ItemsSource = snapshot;
        if (snapshot.Count > 0)
        {
            MyModsGameCombo.SelectedIndex = 0;
            FriendGameCombo.SelectedIndex = 0;
        }

        var totalStale = _games.Sum(g => g.StaleCount);
        SetStatus($"{_games.Count} games · {_games.Sum(g => g.SubCount)} subs · {totalStale} stale.");
    }

    private string? FindLibraryFor(uint appId)
    {
        foreach (var root in _libraryRoots)
        {
            var acf = System.IO.Path.Combine(root, "steamapps", "workshop", $"appworkshop_{appId}.acf");
            if (System.IO.File.Exists(acf)) return root;
        }
        return null;
    }

    private void OnGamesGridDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameRow row) return;
        // Jump to My Mods tab and select that game.
        MyModsGameCombo.SelectedValue = row.AppId;
        MainTabs.SelectedIndex = 1;
    }

    // ===================================================================
    //  My Mods tab
    // ===================================================================

    private async void OnMyModsGameChanged(object sender, SelectionChangedEventArgs e)
    {
        await AuditSelectedGameAsync();
    }

    private async void OnAuditClicked(object sender, RoutedEventArgs e)
        => await AuditSelectedGameAsync(force: true);

    private async Task AuditSelectedGameAsync(bool force = false)
    {
        if (MyModsGameCombo.SelectedValue is not uint appId) return;

        SetStatus($"Auditing {_names.Resolve(appId)}…");
        _myMods.Clear();

        var locals = await Task.Run(() =>
            _enumerator.EnumerateForApp(_libraryRoots, appId).ToList());
        _myModsRaw = locals;

        if (locals.Count == 0)
        {
            SetStatus("No subscriptions for this game.");
            return;
        }

        var remotes = await _api.GetPublishedFileDetailsAsync(locals.Select(l => l.PublishedFileId));

        var rows = locals
            .Select(l => new AuditedItemRow(StalenessAuditor.Audit(
                l,
                remotes.TryGetValue(l.PublishedFileId, out var r) ? r : null,
                _names.Resolve(appId))))
            .OrderBy(r => r.Source.Status switch
            {
                FreshnessStatus.Stale     => 0,
                FreshnessStatus.ApiFailed => 1,
                FreshnessStatus.Unknown   => 2,
                FreshnessStatus.Removed   => 3,
                FreshnessStatus.Current   => 4,
                _ => 5,
            })
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var r in rows) _myMods.Add(r);

        var stale = rows.Count(r => r.Source.Status == FreshnessStatus.Stale);
        SetStatus($"{locals.Count} subscribed · {stale} stale · {rows.Count(r => r.Source.Status == FreshnessStatus.Current)} current.");
    }

    private async void OnRefreshRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AuditedItemRow row)
        {
            await RefreshItemsAsync(new[] { row.LocalItem });
        }
    }

    private async void OnRefreshSelectedClicked(object sender, RoutedEventArgs e)
    {
        var rows = MyModsGrid.SelectedItems.OfType<AuditedItemRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Select one or more rows first.", "Workshop Sentinel");
            return;
        }
        await RefreshItemsAsync(rows.Select(r => r.LocalItem).ToList());
    }

    private async void OnRefreshAllStaleClicked(object sender, RoutedEventArgs e)
    {
        var stale = _myMods.Where(r => r.Source.Status == FreshnessStatus.Stale)
            .Select(r => r.LocalItem).ToList();
        if (stale.Count == 0)
        {
            MessageBox.Show(this, "No stale items in this game.", "Workshop Sentinel");
            return;
        }
        await RefreshItemsAsync(stale);
    }

    private async void OnRefreshAllUnknownClicked(object sender, RoutedEventArgs e)
    {
        // Friends-only / private items return result=9 from the public API, surface as Unknown.
        // The author of those mods often wants to nuke + re-pull them because the API-visible
        // staleness signal isn't available.
        var unknown = _myMods.Where(r => r.Source.Status == FreshnessStatus.Unknown)
            .Select(r => r.LocalItem).ToList();
        if (unknown.Count == 0)
        {
            MessageBox.Show(this, "No friends-only (Unknown) items in this game.", "Workshop Sentinel");
            return;
        }
        await RefreshItemsAsync(unknown);
    }

    private async void OnRefreshAllItemsClicked(object sender, RoutedEventArgs e)
    {
        var all = _myMods.Where(r => r.Source.Status != FreshnessStatus.Removed)
            .Select(r => r.LocalItem).ToList();
        if (all.Count == 0)
        {
            MessageBox.Show(this, "Nothing to refresh.", "Workshop Sentinel");
            return;
        }
        await RefreshItemsAsync(all);
    }

    private async Task RefreshItemsAsync(IReadOnlyList<WorkshopItemLocal> items)
    {
        if (items.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"About to mark {items.Count} item(s) as stale in Steam's local manifest so Steam re-downloads them.\n\n" +
            $"Your local content folder is NOT touched — if Steam doesn't respond, your existing copy stays in place.\n\n" +
            $"Steam can stay running. The mutation triggers a re-pull within ~10 min (or immediately on next Steam launch).\n\n" +
            $"Proceed?",
            "Confirm refresh", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        SetStatus($"Refreshing {items.Count} item(s)…");
        var progress = new Progress<RefreshStep>(s => SetStatus($"[{s.Stage}] {s.PublishedFileId}  {s.Message}"));
        var outcomes = await _refresher.RefreshAsync(items, progress);

        var ok   = outcomes.Count(o => o.Success);
        var fail = outcomes.Count - ok;
        var verifyFailures = outcomes
            .Where(o => !o.Success && (o.Error ?? "").Contains("Verification timeout", StringComparison.Ordinal))
            .ToList();
        var body = $"Refresh complete.\n\nSucceeded (Steam re-pulled the content): {ok}\nFailed: {fail}\n\n";
        if (verifyFailures.Count > 0)
        {
            body += $"{verifyFailures.Count} item(s) didn't verify within the window. Steam may not be running, or its " +
                    "sub-monitor hasn't ticked yet (~10 min worst case). YOUR LOCAL CONTENT IS UNTOUCHED — the old " +
                    "copy is still on disk. Options: start Steam (mutation triggers re-pull on launch), wait for the " +
                    "next sub-monitor tick, or re-run the refresh.";
        }
        else if (fail > 0)
        {
            body += "Failed items left a tip on the status bar.";
        }
        else
        {
            body += "All items verified — Steam re-pulled fresh content.";
        }
        MessageBox.Show(this, body, "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Information);

        // Re-audit so the grid reflects the new state.
        await AuditSelectedGameAsync(force: true);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)        return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ===================================================================
    //  Friends Compare tab
    // ===================================================================

    private async void OnAddFriendClicked(object sender, RoutedEventArgs e)
        => await AddFriendFromInputAsync();

    private async void OnFriendInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await AddFriendFromInputAsync(); }
    }

    private async Task AddFriendFromInputAsync()
    {
        var raw = FriendInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (FriendGameCombo.SelectedValue is not uint appId)
        {
            MessageBox.Show(this, "Pick a game first.", "Workshop Sentinel");
            return;
        }

        SetStatus($"Resolving '{raw}'…");
        var friend = await _friendClient.ResolveAsync(raw);
        if (friend is null)
        {
            MessageBox.Show(this, $"Couldn't resolve '{raw}' to a Steam profile.\nCheck the spelling and that the profile exists.", "Workshop Sentinel");
            SetStatus("Ready.");
            return;
        }

        if (_friends.Any(f => f.SteamId64 == friend.SteamId64))
        {
            MessageBox.Show(this, $"'{friend.DisplayName ?? friend.SteamId64}' is already added.", "Workshop Sentinel");
            return;
        }

        SetStatus($"Fetching {friend.DisplayName ?? friend.SteamId64}'s subs for {_names.Resolve(appId)}…");
        var sub = await _friendClient.FetchSubscriptionsAsync(friend, appId);
        if (sub.Outcome == FriendScrapeOutcome.ProfilePrivate)
        {
            MessageBox.Show(this,
                $"{friend.DisplayName ?? friend.SteamId64}'s profile (or Workshop section) is private.\n\n" +
                "They need to set Profile + Game Details + Inventory to Public for this to work.",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("Ready.");
            return;
        }
        if (sub.Outcome == FriendScrapeOutcome.NetworkError)
        {
            MessageBox.Show(this, $"Network error while fetching subs: {sub.ErrorDetail}", "Workshop Sentinel",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Ready.");
            return;
        }

        _friends.Add(friend);
        _friendSubs[friend.SteamId64] = new HashSet<ulong>(sub.PublishedFileIds);
        FriendInput.Clear();

        await RebuildFriendsCompareAsync();
    }

    private async void OnClearFriendsClicked(object sender, RoutedEventArgs e)
    {
        _friends.Clear();
        _friendSubs.Clear();
        await RebuildFriendsCompareAsync();
    }

    private async void OnReloadMySubsClicked(object sender, RoutedEventArgs e)
        => await RebuildFriendsCompareAsync();

    private async Task RebuildFriendsCompareAsync()
    {
        if (FriendGameCombo.SelectedValue is not uint appId)
        {
            FriendsGrid.ItemsSource = null;
            return;
        }

        SetStatus($"Building compare view for {_names.Resolve(appId)}…");

        // My local subs for this game.
        var myLocals = await Task.Run(() =>
            _enumerator.EnumerateForApp(_libraryRoots, appId).ToList());
        var mineSet  = myLocals.Select(l => l.PublishedFileId).ToHashSet();

        // Union of every publishedfileid across me + every added friend.
        var allIds = new HashSet<ulong>(mineSet);
        foreach (var sub in _friendSubs.Values) allIds.UnionWith(sub);
        if (allIds.Count == 0)
        {
            FriendsGrid.ItemsSource = null;
            FriendsGrid.Columns.Clear();
            SetStatus("No items in the union — add a friend or pick a different game.");
            return;
        }

        // Fetch titles for every ID in the union (so the grid shows names, not bare numbers).
        var remotes = await _api.GetPublishedFileDetailsAsync(allIds);

        // Rebuild columns. First = mod title, then "You", then one per friend.
        FriendsGrid.Columns.Clear();
        FriendsGrid.ItemsSource = null;
        FriendsGrid.Columns.Add(new DataGridTextColumn
        {
            Header  = "Mod",
            Binding = new Binding("[Title]"),
            Width   = new DataGridLength(3, DataGridLengthUnitType.Star),
        });
        FriendsGrid.Columns.Add(new DataGridTextColumn
        {
            Header  = "ID",
            Binding = new Binding("[Id]"),
            Width   = new DataGridLength(120),
        });
        FriendsGrid.Columns.Add(MakeMarkColumn("You", "[You]"));
        foreach (var f in _friends)
        {
            FriendsGrid.Columns.Add(MakeMarkColumn(
                f.DisplayName ?? f.VanitySlug ?? f.SteamId64,
                $"[f{f.SteamId64}]"));
        }
        // Action column: per-row Subscribe button shown only when the local user doesn't
        // already own the mod. Click POSTs to Steam with the user's CEF cookies.
        FriendsGrid.Columns.Add(MakeSubscribeColumn());

        // Build rows. Use a Dictionary<string,string> per row so column bindings can be string-keyed.
        var rows = allIds
            .Select(id =>
            {
                remotes.TryGetValue(id, out var r);
                var d = new Dictionary<string, string>
                {
                    ["Title"] = r?.Title ?? $"#{id}",
                    ["Id"]    = id.ToString(),
                    ["You"]   = mineSet.Contains(id) ? "✓" : "·",
                };
                foreach (var f in _friends)
                {
                    d[$"f{f.SteamId64}"] = _friendSubs[f.SteamId64].Contains(id) ? "✓" : "·";
                }
                // Action: empty when the user already owns it (button hidden) or no friend
                // has it (no reason to subscribe); "Subscribe" otherwise.
                var anyFriendHas = _friends.Any(f => _friendSubs[f.SteamId64].Contains(id));
                d["Action"] = (!mineSet.Contains(id) && anyFriendHas) ? "Subscribe" : "";
                return d;
            })
            .OrderBy(d => d["Title"], StringComparer.OrdinalIgnoreCase)
            .ToList();

        FriendsGrid.ItemsSource = rows;
        SetStatus($"{rows.Count} mod(s) across you + {_friends.Count} friend(s).");
    }

    // --- Steam friends picker (left panel of Compare tab) ---

    private void ReloadFriendRoster()
    {
        if (_friendsResolver is null)
        {
            SetStatus("Steam not found — friend roster unavailable.");
            return;
        }

        var favSet = new HashSet<string>(_settings.FavoriteFriendSteamIds, StringComparer.Ordinal);
        var roster = _friendsResolver.Resolve();
        if (roster.Count == 0)
        {
            SetStatus("No friends found in Steam's localconfig.vdf. (Has the Steam client been opened on this PC?)");
            return;
        }

        foreach (var f in roster) f.IsFavorite = favSet.Contains(f.SteamId64);

        _friendRoster.Clear();
        foreach (var f in roster) _friendRoster.Add(new FriendRow(f));
        ResortFriendRoster();
        SetStatus($"Loaded {_friendRoster.Count} Steam friend(s) from localconfig.vdf.");
    }

    private void ResortFriendRoster()
    {
        var sorted = _friendRoster.OrderBy(r => r.SortKey).ToList();
        _friendRoster.Clear();
        foreach (var r in sorted) _friendRoster.Add(r);
    }

    private void OnReloadFriendListClicked(object sender, RoutedEventArgs e)
        => ReloadFriendRoster();

    private async void OnRefreshOnlineStatusClicked(object sender, RoutedEventArgs e)
    {
        if (_friendRoster.Count == 0) { ReloadFriendRoster(); if (_friendRoster.Count == 0) return; }

        SetStatus($"Fetching online status for {_friendRoster.Count} friend(s)…");
        // Cap concurrency — Steam profile pages are cheap but ~100 simultaneous requests is rude.
        var sem = new SemaphoreSlim(8);
        var snapshot = _friendRoster.ToList();
        var tasks = snapshot.Select(async row =>
        {
            await sem.WaitAsync();
            try
            {
                var state = await _friendClient.FetchOnlineStateAsync(row.SteamId64);
                await Dispatcher.InvokeAsync(() => row.Online = state);
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        ResortFriendRoster();
        var online = _friendRoster.Count(r => r.Online == FriendOnlineState.Online || r.Online == FriendOnlineState.InGame);
        SetStatus($"Online status refreshed. {online} friend(s) currently online.");
    }

    private void OnFriendFavoriteToggled(object sender, RoutedEventArgs e)
    {
        // ToggleButton has already flipped IsChecked (and the binding pushed it into IsFavorite).
        // Persist the new favorite set + re-sort the list so the toggled row jumps into place.
        _settings.FavoriteFriendSteamIds = _friendRoster
            .Where(r => r.IsFavorite)
            .Select(r => r.SteamId64)
            .ToList();
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) { SetStatus($"Couldn't save favorites: {ex.Message}"); return; }

        ResortFriendRoster();
    }

    private async void OnAddFriendFromListClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FriendRow row) return;
        if (FriendGameCombo.SelectedValue is not uint appId)
        {
            MessageBox.Show(this, "Pick a game first.", "Workshop Sentinel");
            return;
        }

        if (_friends.Any(f => f.SteamId64 == row.SteamId64))
        {
            MessageBox.Show(this, $"'{row.PersonaName}' is already added.", "Workshop Sentinel");
            return;
        }

        var friend = new FriendIdentity(row.SteamId64, null, row.PersonaName);
        SetStatus($"Fetching {row.PersonaName}'s subs for {_names.Resolve(appId)}…");
        var sub = await _friendClient.FetchSubscriptionsAsync(friend, appId);

        if (sub.Outcome == FriendScrapeOutcome.ProfilePrivate)
        {
            MessageBox.Show(this,
                $"{row.PersonaName}'s profile (or Workshop section) is private.\n\n" +
                "They'd need to set Profile + Game Details + Inventory to Public for this to work.",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("Ready.");
            return;
        }
        if (sub.Outcome == FriendScrapeOutcome.NetworkError)
        {
            MessageBox.Show(this, $"Network error: {sub.ErrorDetail}", "Workshop Sentinel",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Ready.");
            return;
        }

        _friends.Add(friend);
        _friendSubs[friend.SteamId64] = new HashSet<ulong>(sub.PublishedFileIds);
        await RebuildFriendsCompareAsync();
    }

    private static DataGridTextColumn MakeMarkColumn(string header, string indexerPath)
    {
        return new DataGridTextColumn
        {
            Header              = header,
            Binding             = new Binding(indexerPath),
            Width               = new DataGridLength(110),
            ElementStyle        = MakeCenteredStyle(),
        };
    }

    private static Style MakeCenteredStyle()
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, 15.0));
        return s;
    }

    /// <summary>
    /// Per-row Subscribe button. The cell is empty for mods the local user already owns
    /// (`[You]` == "✓") and a clickable "Subscribe" button otherwise. Click handler reads
    /// Steam's CEF cookies, POSTs to steamcommunity.com/sharedfiles/subscribe, and on
    /// success updates the row's `[You]` to "↻" so the user sees immediate feedback —
    /// Steam's local ACF doesn't actually flip to "✓" until its next sub-monitor tick.
    /// </summary>
    private DataGridTemplateColumn MakeSubscribeColumn()
    {
        var template = new DataTemplate();
        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetBinding(Button.ContentProperty, new Binding("[Action]"));
        btn.SetValue(Button.PaddingProperty, new Thickness(8, 2, 8, 2));
        // Hide for owned rows: when [Action] is empty string the button collapses cleanly.
        btn.SetBinding(Button.VisibilityProperty, new Binding("[Action]")
        {
            Converter = new EmptyStringToVisibilityConverter(),
        });
        btn.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnSubscribeRowClicked));
        template.VisualTree = btn;
        return new DataGridTemplateColumn
        {
            Header       = "Action",
            CellTemplate = template,
            Width        = new DataGridLength(130),
        };
    }

    private async void OnSubscribeRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not Dictionary<string, string> row)
            return;
        if (!ulong.TryParse(row["Id"], out var publishedFileId)) return;
        if (FriendGameCombo.SelectedValue is not uint appId) return;

        // Lazy-acquire cookies (one DPAPI/SQLite read covers every subscribe in the session).
        if (!await EnsureSubscribeClientAsync()) return;

        btn.IsEnabled = false;
        var prevContent = btn.Content;
        btn.Content = "…";
        SetStatus($"Subscribing to {row["Title"]} ({publishedFileId})…");
        var result = await _subscribeClient!.SubscribeAsync(appId, publishedFileId);

        if (result.Success)
        {
            row["Action"] = "";
            row["You"]    = "↻";   // pending Steam-client sync; not yet "✓"
            // Force the grid to re-render this row by reassigning the same source.
            var src = FriendsGrid.ItemsSource;
            FriendsGrid.ItemsSource = null;
            FriendsGrid.ItemsSource = src;
            SetStatus($"Subscribed. Steam will pull this item on its next sub-monitor tick (~10 min) or on restart.");
        }
        else
        {
            btn.Content   = prevContent;
            btn.IsEnabled = true;
            MessageBox.Show(this,
                $"Subscribe failed: {result.Outcome}\n\n{result.ErrorDetail}",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("Ready.");
        }
    }

    /// <summary>
    /// Initialize the subscribe HTTP client + Steam session cookies on first use.
    /// Surfaces a single, friendly error dialog per outcome instead of erroring per-click.
    /// </summary>
    private async Task<bool> EnsureSubscribeClientAsync()
    {
        if (_subscribeClient is not null) return true;
        var reader = new SteamCefCookieReader();
        var cookieResult = await Task.Run(reader.Read);
        if (cookieResult.Outcome != SteamCefCookieOutcome.Ok || cookieResult.Cookies is null)
        {
            var message = cookieResult.Outcome switch
            {
                SteamCefCookieOutcome.SteamNotInstalled =>
                    "Steam doesn't appear to be installed on this machine, or its htmlcache files are missing.",
                SteamCefCookieOutcome.MasterKeyDecryptFailed =>
                    "Couldn't decrypt Steam's cookie store. This usually means you're running Workshop Sentinel under a different Windows user than the one that owns the Steam install.",
                SteamCefCookieOutcome.SqliteOpenFailed =>
                    "Couldn't open Steam's cookie database. Try closing Steam fully and re-launching it once, then retry.",
                SteamCefCookieOutcome.MissingRequiredCookies =>
                    "Steam's cookie store doesn't have the auth cookies we need. " +
                    "Open Steam, log in, click any Workshop page once in the overlay or library browser, then retry.",
                _ => "Unknown cookie-read failure.",
            };
            MessageBox.Show(this,
                $"{message}\n\nDetails: {cookieResult.ErrorDetail}",
                "Steam cookies", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _subscribeClient = new SteamSubscribeClient(_http, cookieResult.Cookies);
        return true;
    }

    /// <summary>Hides the Subscribe button when its bound `[Action]` is empty string.</summary>
    private sealed class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // ===================================================================
    //  Self-update
    // ===================================================================
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await _updateChecker.CheckAsync(Program.Version);
            await Dispatcher.InvokeAsync(() => ApplyUpdateCheck(result));
        }
        catch
        {
            // The checker swallows expected exceptions; anything reaching here is unexpected.
            // Don't take down the GUI — the existing version label remains accurate.
        }
    }

    private void ApplyUpdateCheck(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateStatus.Latest:
                _pendingUpdate = null;
                UpdateStatusLabel.Text  = "(latest)";
                UpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                UpdateButton.Visibility = Visibility.Collapsed;
                UpdateBanner.Visibility = Visibility.Collapsed;
                break;
            case UpdateStatus.UpdateAvailable when result.LatestVersion is not null
                                              && !string.IsNullOrEmpty(result.DownloadUrl):
                _pendingUpdate = result;
                UpdateStatusLabel.Text  = $"— v{result.LatestVersion} available";
                UpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x9B, 0xFF));
                UpdateButton.Visibility = Visibility.Visible;
                UpdateBannerText.Text   = $"Workshop Sentinel v{result.LatestVersion} is available.";
                UpdateBanner.Visibility = _updateBannerDismissed ? Visibility.Collapsed : Visibility.Visible;
                break;
            case UpdateStatus.CheckFailed:
            default:
                _pendingUpdate = null;
                UpdateStatusLabel.Text  = "(update check failed)";
                UpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                UpdateButton.Visibility = Visibility.Collapsed;
                UpdateBanner.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OnUpdateBannerLaterClicked(object sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = true;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _pendingUpdate.DownloadUrl is null) return;
        var target = _pendingUpdate;

        var confirm = MessageBox.Show(this,
            $"Update to v{target.LatestVersion}? The app will restart.",
            "Workshop Sentinel", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show(this, "Couldn't resolve the running executable path. Update aborted.",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdateButton.IsEnabled = false;
        SetStatus($"Downloading v{target.LatestVersion}…");
        var progress = new Progress<double>(p =>
            SetStatus($"Downloading v{target.LatestVersion}… {(int)(p * 100)}%"));

        var result = await _updateInstaller.DownloadAndSwapAsync(
            exePath, target.DownloadUrl, target.AssetSha256, progress);

        if (!result.Success)
        {
            UpdateButton.IsEnabled = true;
            SetStatus("Update failed.");
            MessageBox.Show(this,
                $"Update failed: {result.Error}\n\nThe running app is unchanged. You can try again, or download v{target.LatestVersion} manually from the Releases page.",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetStatus($"Update installed — restarting…");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = exePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Update installed but relaunch failed: {ex.Message}\n\nClose this window and start WorkshopSentinel.exe manually.",
                "Workshop Sentinel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Application.Current.Shutdown();
    }

    // ===================================================================
    //  shared
    // ===================================================================
    private void SetStatus(string msg)
    {
        if (Dispatcher.CheckAccess()) StatusLabel.Text = msg;
        else Dispatcher.Invoke(() => StatusLabel.Text = msg);
    }
}
