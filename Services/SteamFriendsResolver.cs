using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WorkshopSentinel.Services;

public enum FriendOnlineState
{
    Unknown,    // not yet scraped
    Offline,
    Online,
    InGame,     // returned as "online" by Steam's XML endpoint with a gameextrainfo field
}

public sealed class SteamFriend
{
    public string SteamId64 { get; init; } = "";
    public uint   AccountId32 { get; init; }
    public string PersonaName { get; init; } = "";
    public string? Avatar { get; init; }
    public FriendOnlineState Online { get; set; } = FriendOnlineState.Unknown;
    public bool   IsFavorite { get; set; }   // app-side (Workshop Sentinel) favorite, persisted to settings
}

/// <summary>
/// Pulls the friend roster out of Steam's local userdata. Specifically:
/// <c>&lt;SteamRoot&gt;/userdata/&lt;accountid32&gt;/config/localconfig.vdf</c>, the
/// <c>"friends"</c> sub-object. Each friend entry is keyed by their <em>accountid32</em>
/// (a 32-bit Steam ID) — not SteamID64. The 64-bit form is reconstructed by adding the
/// constant <c>0x0110000100000000</c> = <c>76561197960265728</c>.
///
/// The current logged-in user appears in the same dict as one of the entries; we skip them.
///
/// Steam-side per-friend favorites are NOT in this file (or anywhere in plain-text VDFs in
/// modern Steam — they live in the Friends UI's Chromium IndexedDB). The user marks their
/// own favorites inside Workshop Sentinel; those are persisted in settings.json.
/// </summary>
public sealed class SteamFriendsResolver
{
    private const ulong SteamId64Base = 76561197960265728UL;
    private readonly string _steamRoot;

    public SteamFriendsResolver(string steamRoot) { _steamRoot = steamRoot; }

    /// <summary>
    /// Find every userdata account dir, pick the most-recently-modified one (heuristic for
    /// "the account the user is currently logged into"), and parse its friends list. Returns
    /// an empty list rather than throwing if Steam isn't found or has never been opened.
    /// </summary>
    public IReadOnlyList<SteamFriend> Resolve()
    {
        var userdata = Path.Combine(_steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return Array.Empty<SteamFriend>();

        // Prefer the most-recently-modified account dir — heuristic for "currently active".
        var accountDirs = Directory.GetDirectories(userdata)
            .Where(d => uint.TryParse(Path.GetFileName(d), out _))
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .ToList();

        var combined = new Dictionary<string, SteamFriend>();   // dedupe by SteamID64 across accounts
        foreach (var dir in accountDirs)
        {
            var configPath = Path.Combine(dir, "config", "localconfig.vdf");
            if (!File.Exists(configPath)) continue;

            uint selfAccountId = uint.TryParse(Path.GetFileName(dir), out var a) ? a : 0;
            foreach (var f in ParseLocalConfig(configPath, selfAccountId))
            {
                combined.TryAdd(f.SteamId64, f);
            }
        }

        return combined.Values
            .OrderBy(f => f.PersonaName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Pure parser. Reads a localconfig.vdf file, returns every entry under
    /// <c>UserLocalConfigStore.friends</c> that's a friend (i.e. a sub-object keyed by an
    /// accountid32 numeric string, excluding the logged-in user themselves).
    /// </summary>
    public static IEnumerable<SteamFriend> ParseLocalConfig(string path, uint selfAccountId)
    {
        AcfNode root;
        try { root = AcfNode.ParseFile(path); }
        catch { yield break; }

        var friendsNode = root["friends"];
        if (friendsNode is null || !friendsNode.IsObject) yield break;

        foreach (var (key, body) in friendsNode.Children)
        {
            if (!body.IsObject) continue;                        // scalar leaves like PersonaName
            if (!uint.TryParse(key, out var accountId)) continue; // entries like "communitypreferences"
            if (accountId == selfAccountId) continue;            // skip self

            var name = body["name"]?.AsString();
            if (string.IsNullOrEmpty(name)) continue;

            yield return new SteamFriend
            {
                AccountId32 = accountId,
                SteamId64   = AccountIdToSteamId64(accountId),
                PersonaName = name,
                Avatar      = body["avatar"]?.AsString(),
            };
        }
    }

    public static string AccountIdToSteamId64(uint accountId32)
        => (SteamId64Base + accountId32).ToString();
}
