using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.Cli.Verbs;

/// <summary>
/// `selfupdate [--yes] [--json]` — headless analogue of the GUI's auto-update banner.
/// Without --yes it only reports; exit 10 signals "update available" so a calling script
/// (e.g. cron over SSH) can decide whether to re-invoke with --yes. After a successful
/// install the process exits WITHOUT relaunching — a daemon over SSH can't re-attach a
/// console, and the next manual invocation will exec the swapped-in binary.
/// </summary>
public static class SelfUpdateCommand
{
    public enum Outcome
    {
        Latest,        // exit 0
        Updated,       // exit 0
        Available,     // exit 10
        Failed,        // exit 1
    }

    public static int ExitCodeFor(Outcome outcome) => outcome switch
    {
        Outcome.Latest    => 0,
        Outcome.Updated   => 0,
        Outcome.Available => 10,
        Outcome.Failed    => 1,
        _ => 1,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var install = false;
        var asJson = false;
        foreach (var a in args)
        {
            switch (a.ToLowerInvariant())
            {
                case "--yes":  install = true; break;
                case "--json": asJson  = true; break;
                default:
                    Console.Error.WriteLine($"selfupdate: unknown flag '{a}'.");
                    return 2;
            }
        }

        using var http = BuildHttpClient();
        var checker = new UpdateChecker(http);

        var current = Program.Version;
        var check = await checker.CheckAsync(current, forceRefresh: true).ConfigureAwait(false);

        if (check.Status == UpdateStatus.CheckFailed)
            return Report(Outcome.Failed, current, latestVersion: null, error: check.ErrorMessage ?? "update check failed", asJson);

        if (check.Status == UpdateStatus.Latest)
            return Report(Outcome.Latest, current, check.LatestVersion, error: null, asJson);

        // UpdateAvailable from here on.
        if (!install)
            return Report(Outcome.Available, current, check.LatestVersion, error: null, asJson);

        if (string.IsNullOrEmpty(check.DownloadUrl))
            return Report(Outcome.Failed, current, check.LatestVersion, error: "release payload missing download url", asJson);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return Report(Outcome.Failed, current, check.LatestVersion, error: "couldn't resolve running executable path", asJson);

        var installer = new UpdateInstaller(http);
        IProgress<double>? progress = asJson
            ? null
            : new Progress<double>(p => Console.WriteLine($"[selfupdate] {(int)(p * 100)}%"));

        var result = await installer.DownloadAndSwapAsync(
            exePath, check.DownloadUrl, check.AssetSha256, progress).ConfigureAwait(false);

        if (!result.Success)
            return Report(Outcome.Failed, current, check.LatestVersion, error: result.Error ?? "download/swap failed", asJson);

        return Report(Outcome.Updated, current, check.LatestVersion, error: null, asJson);
    }

    private static int Report(Outcome outcome, string currentVersion, string? latestVersion, string? error, bool asJson)
    {
        if (asJson)
        {
            var payload = new
            {
                status = outcome switch
                {
                    Outcome.Latest    => "latest",
                    Outcome.Updated   => "updated",
                    Outcome.Available => "available",
                    _                 => "failed",
                },
                currentVersion,
                latestVersion,
                error,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
        }
        else
        {
            switch (outcome)
            {
                case Outcome.Latest:
                    Console.WriteLine($"selfupdate: already at latest (v{currentVersion}).");
                    break;
                case Outcome.Available:
                    Console.WriteLine($"selfupdate: v{latestVersion} available (currently v{currentVersion}). Re-run with --yes to install.");
                    break;
                case Outcome.Updated:
                    Console.WriteLine($"selfupdate: installed v{latestVersion}. Re-run any verb to exec the new binary.");
                    break;
                case Outcome.Failed:
                    Console.Error.WriteLine($"selfupdate: failed — {error}");
                    break;
            }
        }
        return ExitCodeFor(outcome);
    }

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"WorkshopSentinel/{Program.Version}");
        return c;
    }
}
