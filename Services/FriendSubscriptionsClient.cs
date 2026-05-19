using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopSentinel.Services;

/// <summary>
/// Resolution of "what does the user want to scrape" into a stable canonical SteamID64.
/// Inputs we accept (in order of how often the user will paste them):
///   • a 17-digit SteamID64 (`76561198...`)                       → returned as-is
///   • a vanity slug (`ensrick`)                                   → resolved via /id/&lt;slug&gt;/?xml=1
///   • a full profile URL (/id/&lt;slug&gt; or /profiles/&lt;sid&gt;) → parsed, then per above
/// </summary>
public sealed record FriendIdentity(string SteamId64, string? VanitySlug, string? DisplayName)
{
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId64}";
}

public enum FriendScrapeOutcome
{
    Ok,
    ProfilePrivate,         // profile / games / Workshop subs are hidden
    ResolveFailed,          // vanity → SteamID64 lookup didn't work
    NetworkError,
    NoSubscriptions,        // public but no public subs for this appid
}

/// <summary>
/// Steam's structured profile visibility, parsed from `&lt;visibilityState&gt;` in the
/// XML profile endpoint. This replaces the brittle HTML phrase regex — Steam's modern
/// HTML renders "public profile with zero subs" and "friends-only profile with subs
/// hidden" identically (no detectable phrase, no class name), so the only reliable
/// signal is the XML.
/// </summary>
public enum ProfileVisibility
{
    Unknown = 0,
    Private = 1,
    FriendsOnly = 2,
    Public = 3,
}

public sealed record FriendSubscriptionResult(
    FriendIdentity? Friend,
    FreshnessStatus Status,                  // unused for friends; kept for downstream uniformity
    FriendScrapeOutcome Outcome,
    IReadOnlyList<ulong> PublishedFileIds,
    string? ErrorDetail);

/// <summary>
/// Scrapes a user's public Workshop subscriptions for a given Steam appid. NO API key
/// required — but the friend's profile + Workshop visibility must be "Public".
///
/// Endpoint: `https://steamcommunity.com/profiles/&lt;steamid64&gt;/myworkshopfiles?section=mysubscriptions&amp;appid=&lt;appid&gt;&amp;p=&lt;n&gt;`
/// (30 items / page; we paginate until a page yields zero new IDs).
///
/// Vanity → SteamID64 resolution uses the public XML endpoint:
///   `https://steamcommunity.com/id/&lt;vanity&gt;/?xml=1` → contains `&lt;steamID64&gt;76561...&lt;/steamID64&gt;`
/// </summary>
public sealed class FriendSubscriptionsClient
{
    private const int MaxPages = 20;   // 30/page × 20 = 600 subs — generous ceiling
    private static readonly Regex FilePathIdRegex =
        new(@"sharedfiles/filedetails/\?id=(\d+)", RegexOptions.Compiled);
    private static readonly Regex SteamId64Regex =
        new(@"<steamID64>(\d{17})</steamID64>", RegexOptions.Compiled);
    private static readonly Regex PersonaNameRegex =
        new(@"<steamID><!\[CDATA\[([^\]]*)\]\]></steamID>", RegexOptions.Compiled);
    // visibilityState: 1=Private, 2=FriendsOnly, 3=Public. This is the structured signal —
    // Steam's HTML can't distinguish "public/empty" from "friends-only/hidden", but the XML always can.
    private static readonly Regex VisibilityStateRegex =
        new(@"<visibilityState>\s*(\d+)\s*</visibilityState>", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public FriendSubscriptionsClient(HttpClient http) { _http = http; }

    /// <summary>
    /// Resolve any user-facing identifier (SteamID64, vanity, profile URL) to a
    /// canonical SteamID64 + display name. Returns null on failure (network or vanity
    /// not found).
    /// </summary>
    public async Task<FriendIdentity?> ResolveAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var (steamId64, vanity) = ParseIdentityInput(input.Trim());

        if (steamId64 is not null)
        {
            // No need to round-trip; still try to fetch a display name as a courtesy.
            var name = await TryGetPersonaNameAsync(steamId64, ct).ConfigureAwait(false);
            return new FriendIdentity(steamId64, vanity, name);
        }
        if (vanity is null) return null;

        try
        {
            var url = $"https://steamcommunity.com/id/{Uri.EscapeDataString(vanity)}/?xml=1";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var sidMatch = SteamId64Regex.Match(body);
            if (!sidMatch.Success) return null;
            var nameMatch = PersonaNameRegex.Match(body);
            return new FriendIdentity(sidMatch.Groups[1].Value, vanity,
                nameMatch.Success ? nameMatch.Groups[1].Value : null);
        }
        catch (HttpRequestException)            { return null; }
        catch (TaskCanceledException)           { return null; }
    }

    /// <summary>
    /// Fetch every published-file ID the friend is publicly subscribed to for the given
    /// app. Pre-flights the XML profile endpoint to check <c>visibilityState</c> — if the
    /// profile isn't Public (3), returns <c>ProfilePrivate</c> without paginating. This
    /// replaces the old HTML phrase-regex private-profile detection, which produced false
    /// negatives on modern Steam (the "Profile is private" string no longer appears in HTML).
    /// </summary>
    public async Task<FriendSubscriptionResult> FetchSubscriptionsAsync(
        FriendIdentity friend, uint appId, CancellationToken ct = default)
    {
        // Phase 0: structured visibility probe via the XML profile endpoint.
        var visibility = await FetchVisibilityAsync(friend.SteamId64, ct).ConfigureAwait(false);
        if (visibility == ProfileVisibility.Private || visibility == ProfileVisibility.FriendsOnly)
        {
            return new FriendSubscriptionResult(
                friend, FreshnessStatus.Unknown, FriendScrapeOutcome.ProfilePrivate,
                Array.Empty<ulong>(),
                $"Profile visibility is {visibility} (need Public to scrape Workshop subscriptions).");
        }
        // Unknown visibility (network blip on the XML probe) — proceed to scrape; we'll
        // get a NoSubscriptions or NetworkError outcome rather than a false-positive Private.

        var found = new HashSet<ulong>();
        var foundOrder = new List<ulong>();

        for (int page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            string html;
            try
            {
                var url = $"https://steamcommunity.com/profiles/{friend.SteamId64}/myworkshopfiles" +
                          $"?section=mysubscriptions&appid={appId}&p={page}";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new FriendSubscriptionResult(
                        friend, FreshnessStatus.ApiFailed, FriendScrapeOutcome.NetworkError,
                        Array.Empty<ulong>(), $"HTTP {(int)resp.StatusCode}");
                }
                html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return new FriendSubscriptionResult(
                    friend, FreshnessStatus.ApiFailed, FriendScrapeOutcome.NetworkError,
                    Array.Empty<ulong>(), ex.Message);
            }
            catch (TaskCanceledException)
            {
                return new FriendSubscriptionResult(
                    friend, FreshnessStatus.ApiFailed, FriendScrapeOutcome.NetworkError,
                    Array.Empty<ulong>(), "timeout");
            }

            int beforeCount = found.Count;
            foreach (Match m in FilePathIdRegex.Matches(html))
            {
                if (!ulong.TryParse(m.Groups[1].Value, out var id)) continue;
                if (found.Add(id)) foundOrder.Add(id);
            }
            if (found.Count == beforeCount) break;   // no new IDs on this page → end of list
        }

        if (found.Count == 0)
        {
            return new FriendSubscriptionResult(
                friend, FreshnessStatus.Unknown, FriendScrapeOutcome.NoSubscriptions,
                Array.Empty<ulong>(), null);
        }
        return new FriendSubscriptionResult(
            friend, FreshnessStatus.Current, FriendScrapeOutcome.Ok, foundOrder, null);
    }

    /// <summary>
    /// Probe a profile's visibility via the XML endpoint. Returns Unknown on network
    /// failure or malformed XML — the caller treats Unknown as "proceed and hope" so a
    /// transient blip doesn't get rendered as a permanent privacy false-positive.
    /// </summary>
    public async Task<ProfileVisibility> FetchVisibilityAsync(string steamId64, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/?xml=1";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return ProfileVisibility.Unknown;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseVisibility(body);
        }
        catch (HttpRequestException) { return ProfileVisibility.Unknown; }
        catch (TaskCanceledException) { return ProfileVisibility.Unknown; }
    }

    /// <summary>
    /// Pure parser exposed for tests. Reads `&lt;visibilityState&gt;N&lt;/visibilityState&gt;`
    /// from Steam's XML profile body. Maps: 1=Private, 2=FriendsOnly, 3=Public; everything
    /// else is Unknown.
    /// </summary>
    public static ProfileVisibility ParseVisibility(string xml)
    {
        var m = VisibilityStateRegex.Match(xml);
        if (!m.Success) return ProfileVisibility.Unknown;
        return m.Groups[1].Value switch
        {
            "1" => ProfileVisibility.Private,
            "2" => ProfileVisibility.FriendsOnly,
            "3" => ProfileVisibility.Public,
            _   => ProfileVisibility.Unknown,
        };
    }

    /// <summary>
    /// Scrape online state for a single SteamID64 via the XML profile endpoint.
    /// Returns Unknown on any error (network, private profile, malformed body).
    /// </summary>
    public async Task<FriendOnlineState> FetchOnlineStateAsync(string steamId64, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/?xml=1";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return FriendOnlineState.Unknown;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseOnlineState(body);
        }
        catch (HttpRequestException) { return FriendOnlineState.Unknown; }
        catch (TaskCanceledException) { return FriendOnlineState.Unknown; }
    }

    /// <summary>
    /// Parse Steam's profile XML for the online-state signal. Steam emits one of:
    ///   &lt;onlineState&gt;offline&lt;/onlineState&gt;
    ///   &lt;onlineState&gt;online&lt;/onlineState&gt;
    ///   &lt;onlineState&gt;in-game&lt;/onlineState&gt;
    /// Returns Unknown on private profiles (the XML reports limitations, no state field).
    /// </summary>
    public static FriendOnlineState ParseOnlineState(string xml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<onlineState>\s*([\w\-]+)\s*</onlineState>");
        if (!m.Success) return FriendOnlineState.Unknown;
        return m.Groups[1].Value.ToLowerInvariant() switch
        {
            "online"  => FriendOnlineState.Online,
            "in-game" => FriendOnlineState.InGame,
            "offline" => FriendOnlineState.Offline,
            _         => FriendOnlineState.Unknown,
        };
    }

    private async Task<string?> TryGetPersonaNameAsync(string steamId64, CancellationToken ct)
    {
        try
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/?xml=1";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = PersonaNameRegex.Match(body);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Pure parser exposed for tests. Returns (steamId64, vanity) with exactly one non-null.
    /// </summary>
    public static (string? SteamId64, string? Vanity) ParseIdentityInput(string input)
    {
        // Strip a leading URL prefix.
        var s = input.Trim();
        const string p = "://steamcommunity.com/";
        var idx = s.IndexOf(p, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s.Substring(idx + p.Length);
        // Drop trailing slash + query string.
        var slashTrim = s.IndexOfAny(new[] { '?', '#' });
        if (slashTrim >= 0) s = s.Substring(0, slashTrim);
        s = s.TrimEnd('/');

        // /profiles/<sid>  or  /id/<slug>  or just <sid> or <slug>
        if (s.StartsWith("profiles/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s.Substring("profiles/".Length).Split('/', 2)[0];
            return IsSteamId64(rest) ? (rest, null) : (null, null);
        }
        if (s.StartsWith("id/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s.Substring("id/".Length).Split('/', 2)[0];
            return string.IsNullOrWhiteSpace(rest) ? (null, null) : (null, rest);
        }
        if (IsSteamId64(s)) return (s, null);
        return (null, s);    // bare vanity slug
    }

    private static bool IsSteamId64(string s) =>
        s.Length == 17 && s.All(char.IsDigit) && s.StartsWith("7656", StringComparison.Ordinal);
}
