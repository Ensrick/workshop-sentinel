using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkshopSentinel.Services;

/// <summary>
/// Persistent settings stored as JSON at %APPDATA%\WorkshopSentinel\settings.json (or a
/// path passed via --config). All fields nullable — missing fields fall back to defaults.
/// Forward-compatible: unknown JSON properties are ignored (won't crash on a downgrade).
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("steam_path_override")]
    public string? SteamPathOverride { get; set; }

    [JsonPropertyName("steam_web_api_key")]
    public string? SteamWebApiKey { get; set; }

    /// <summary>Last-used audit filter ("all" / "stale" / "unknown" / "removed").</summary>
    [JsonPropertyName("ui_filter")]
    public string? UiFilter { get; set; }

    /// <summary>
    /// SteamID64s the user has starred in the Compare to Friends tab. Persisted so the
    /// star survives restarts. (Steam's own per-friend favorite flag lives in the Friends
    /// UI's Chromium IndexedDB and isn't reachable from plain-text VDFs.)
    /// </summary>
    [JsonPropertyName("favorite_friend_steamids")]
    public System.Collections.Generic.List<string> FavoriteFriendSteamIds { get; set; } = new();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }

    public SettingsStore(string? overridePath = null)
    {
        Path = overridePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "WorkshopSentinel", "settings.json");
    }

    /// <summary>
    /// Load settings. Missing file → empty Settings. Corrupt JSON → empty Settings + a
    /// `.corrupt-<timestamp>` sidecar copy preserved for recovery. Never throws on read.
    /// </summary>
    public Settings Load()
    {
        if (!File.Exists(Path)) return new Settings();

        try
        {
            var text = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<Settings>(text, JsonOpts) ?? new Settings();
        }
        catch (JsonException)
        {
            // Quarantine the corrupt file so we don't keep failing on the next launch.
            // The user can inspect the sidecar to recover hand-edits if any.
            TryQuarantine();
            return new Settings();
        }
    }

    public void Save(Settings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic write: serialize to a tempfile first, then replace. Avoids leaving a
        // half-written settings.json behind if the process is killed mid-write.
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Move(tmp, Path, overwrite: true);
    }

    private void TryQuarantine()
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(Path, $"{Path}.corrupt-{stamp}", overwrite: false);
        }
        catch
        {
            // Best effort — if we can't move, leave the file in place. The Load() caller
            // already has an empty Settings to work with.
        }
    }
}
