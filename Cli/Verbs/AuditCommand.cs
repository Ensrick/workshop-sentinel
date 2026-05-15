using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.Cli.Verbs;

/// <summary>
/// `audit [--game &lt;appid&gt;] [--stale-only] [--json]` — enumerate local subs, fetch live
/// `time_updated` from the Steam Web API, classify each, render a table or JSON.
/// </summary>
public static class AuditCommand
{
    public static async Task<int> RunAsync(string[] args, SettingsStore? settingsStore = null)
    {
        uint? gameFilter = null;
        var staleOnly = false;
        var asJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--game":
                    if (i + 1 >= args.Length || !uint.TryParse(args[++i], out var g))
                    {
                        Console.Error.WriteLine("audit: --game expects a numeric appid.");
                        return 2;
                    }
                    gameFilter = g;
                    break;
                case "--stale-only":
                    staleOnly = true;
                    break;
                case "--json":
                    asJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"audit: unknown flag '{args[i]}'.");
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
            Console.Error.WriteLine($"audit: {ex.Message}");
            return 3;
        }

        var libraries = new LibraryFoldersResolver().Resolve(steamRoot);
        var enumerator = new WorkshopEnumerator();
        var locals = (gameFilter is uint appId
                ? enumerator.EnumerateForApp(libraries, appId)
                : enumerator.EnumerateAll(libraries))
            .ToList();

        if (locals.Count == 0)
        {
            Console.WriteLine(gameFilter is null
                ? "No subscribed Workshop items found."
                : $"No subscribed Workshop items for app {gameFilter}.");
            return 0;
        }

        if (!asJson)
        {
            Console.WriteLine($"Auditing {locals.Count} item(s) against the Steam Web API...");
        }

        using var http = BuildHttpClient();
        var api = new SteamWebApiClient(http);
        var remotes = await api.GetPublishedFileDetailsAsync(locals.Select(l => l.PublishedFileId)).ConfigureAwait(false);

        var names = new AppNameResolver(libraries);
        var audited = locals
            .Select(l => StalenessAuditor.Audit(
                l,
                remotes.TryGetValue(l.PublishedFileId, out var r) ? r : null,
                names.Resolve(l.AppId)))
            .ToList();

        if (staleOnly)
            audited = audited.Where(a => a.Status == FreshnessStatus.Stale).ToList();

        if (asJson) WriteJson(audited);
        else        WriteTable(audited);

        return 0;
    }

    private static HttpClient BuildHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WorkshopSentinel/{Program.Version}");
        return client;
    }

    private static void WriteJson(IReadOnlyList<AuditedItem> items)
    {
        // Flatten to a render-friendly shape — easier for downstream tools than the nested record.
        var flat = items.Select(i => new
        {
            i.Local.AppId,
            GameName = i.GameName,
            i.Local.PublishedFileId,
            Title = i.Remote?.Title,
            Status = i.Status.ToString(),
            i.Local.LocalTimeUpdated,
            RemoteTimeUpdated = i.Remote?.RemoteTimeUpdated,
            i.Local.LocalSizeBytes,
            RemoteSizeBytes = i.Remote?.RemoteSizeBytes,
            ApiResult = i.Remote?.ApiResult,
        });
        Console.WriteLine(JsonSerializer.Serialize(flat, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteTable(IReadOnlyList<AuditedItem> items)
    {
        if (items.Count == 0)
        {
            Console.WriteLine("(no items match the filter)");
            return;
        }

        var ordered = items
            .OrderBy(i => i.GameName)
            .ThenByDescending(i => StaleDeltaSeconds(i))
            .ThenByDescending(i => i.Local.LocalTimeUpdated)
            .ToList();

        Console.WriteLine($"{"Status",-10}  {"Game",-30}  {"Item",-40}  {"Δ (live − local)",-22}");
        Console.WriteLine(new string('-', 110));
        foreach (var i in ordered)
        {
            var title = i.Remote?.Title ?? $"#{i.Local.PublishedFileId}";
            if (title.Length > 38) title = title[..38] + "…";
            var game = (i.GameName ?? "—").PadRight(30);
            Console.WriteLine($"{i.Status,-10}  {game,-30}  {title,-40}  {FormatDelta(i),-22}");
        }
        Console.WriteLine();
        var counts = items.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine(
            $"Total: {items.Count}  " +
            $"Current: {counts.GetValueOrDefault(FreshnessStatus.Current)}  " +
            $"Stale: {counts.GetValueOrDefault(FreshnessStatus.Stale)}  " +
            $"Unknown: {counts.GetValueOrDefault(FreshnessStatus.Unknown)}  " +
            $"Removed: {counts.GetValueOrDefault(FreshnessStatus.Removed)}  " +
            $"ApiFailed: {counts.GetValueOrDefault(FreshnessStatus.ApiFailed)}");
    }

    private static long StaleDeltaSeconds(AuditedItem i)
    {
        var rt = i.Remote?.RemoteTimeUpdated ?? 0;
        return rt - i.Local.LocalTimeUpdated;
    }

    private static string FormatDelta(AuditedItem i)
    {
        if (i.Status != FreshnessStatus.Stale) return "";
        var delta = StaleDeltaSeconds(i);
        if (delta < 60)         return $"{delta}s newer";
        if (delta < 3600)       return $"{delta / 60}min newer";
        if (delta < 86400)      return $"{delta / 3600}h newer";
        return $"{delta / 86400}d newer";
    }
}
