using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.Cli.Verbs;

/// <summary>
/// `list [--game &lt;appid&gt;] [--json]` — enumerate every locally-subscribed Workshop item.
/// No network calls. Output is either a fixed-width table (default) or JSON to stdout.
/// </summary>
public static class ListCommand
{
    public static int Run(string[] args, SettingsStore? settingsStore = null)
    {
        uint? gameFilter = null;
        var asJson = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--game":
                    if (i + 1 >= args.Length || !uint.TryParse(args[++i], out var g))
                    {
                        Console.Error.WriteLine("list: --game expects a numeric appid.");
                        return 2;
                    }
                    gameFilter = g;
                    break;
                case "--json":
                    asJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"list: unknown flag '{args[i]}'.");
                    return 2;
            }
        }

        settingsStore ??= new SettingsStore();
        var settings = settingsStore.Load();
        var pathResolver = new SteamPathResolver(settings);

        string steamRoot;
        try { steamRoot = pathResolver.Resolve(); }
        catch (SteamNotFoundException ex)
        {
            Console.Error.WriteLine($"list: {ex.Message}");
            return 3;
        }

        var libraries = new LibraryFoldersResolver().Resolve(steamRoot);
        var enumerator = new WorkshopEnumerator();

        var items = gameFilter is uint appId
            ? enumerator.EnumerateForApp(libraries, appId).ToList()
            : enumerator.EnumerateAll(libraries).ToList();

        if (asJson)
        {
            WriteJson(items);
            return 0;
        }

        WriteTable(items, gameFilter);
        return 0;
    }

    private static void WriteJson(List<WorkshopItemLocal> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        Console.WriteLine(json);
    }

    private static void WriteTable(List<WorkshopItemLocal> items, uint? gameFilter)
    {
        if (items.Count == 0)
        {
            Console.WriteLine(gameFilter is null
                ? "No subscribed Workshop items found across any library."
                : $"No subscribed Workshop items for app {gameFilter}.");
            return;
        }

        // Group by AppId so the output is scannable when listing across all games.
        var ordered = items
            .OrderBy(i => i.AppId)
            .ThenByDescending(i => i.LocalTimeUpdated)
            .ToList();

        Console.WriteLine($"{"AppId",-8}  {"PublishedFileId",-12}  {"Updated (UTC)",-20}  {"Size",-10}  Manifest");
        Console.WriteLine(new string('-', 78));
        foreach (var i in ordered)
        {
            var when = i.LocalTimeUpdated > 0
                ? DateTimeOffset.FromUnixTimeSeconds(i.LocalTimeUpdated).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "—";
            Console.WriteLine($"{i.AppId,-8}  {i.PublishedFileId,-12}  {when,-20}  {FormatSize(i.LocalSizeBytes),-10}  {i.LocalManifest}");
        }
        Console.WriteLine();
        Console.WriteLine($"Total: {ordered.Count} item(s).");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024)       return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0,0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0,0:F1} MB";
    }
}
