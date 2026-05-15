using System;
using System.IO;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WorkshopSentinelTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_returns_empty_settings_when_file_missing()
    {
        var store = new SettingsStore(_settingsPath);

        var s = store.Load();

        Assert.NotNull(s);
        Assert.Null(s.SteamPathOverride);
        Assert.Null(s.SteamWebApiKey);
    }

    [Fact]
    public void Save_then_Load_round_trips_all_fields()
    {
        var store = new SettingsStore(_settingsPath);
        var s = new Settings
        {
            SteamPathOverride = @"D:\Games\Steam",
            SteamWebApiKey = "ABCDEF1234567890",
            UiFilter = "stale",
        };

        store.Save(s);
        var roundTripped = store.Load();

        Assert.Equal(@"D:\Games\Steam", roundTripped.SteamPathOverride);
        Assert.Equal("ABCDEF1234567890", roundTripped.SteamWebApiKey);
        Assert.Equal("stale", roundTripped.UiFilter);
    }

    [Fact]
    public void Load_with_corrupt_json_returns_empty_settings_and_quarantines_file()
    {
        File.WriteAllText(_settingsPath, "{this is not json");
        var store = new SettingsStore(_settingsPath);

        var s = store.Load();

        Assert.Null(s.SteamPathOverride);
        // Original file moved aside; primary path no longer corrupt.
        Assert.False(File.Exists(_settingsPath));
        var quarantined = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.Single(quarantined);
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        var nested = Path.Combine(_tempDir, "deep", "nested", "settings.json");
        var store = new SettingsStore(nested);

        store.Save(new Settings { UiFilter = "all" });

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Load_ignores_unknown_json_properties()
    {
        // Forward-compat: a newer version writes an extra field; older binary should still load.
        File.WriteAllText(_settingsPath, """
            {
              "steam_path_override": "C:/Steam",
              "future_field": { "nested": true, "list": [1,2,3] }
            }
            """);
        var store = new SettingsStore(_settingsPath);

        var s = store.Load();

        Assert.Equal("C:/Steam", s.SteamPathOverride);
    }
}
