using System;
using System.Collections.Generic;
using System.Linq;
using WorkshopSentinel.Cli.Verbs;
using WorkshopSentinel.Services;

namespace WorkshopSentinel.Cli;

/// <summary>
/// Dispatches a verb from argv to its handler. Exit codes mirror the VMBLauncher convention:
/// 0 = success, 1 = runtime error, 2 = bad usage, 3 = preflight failed.
/// </summary>
public static class CliRunner
{
    public static int Run(string[] args)
    {
        var (cleaned, flags) = ExtractGlobalFlags(args);
        // Auto-suppress the banner when a JSON output flag is present anywhere in argv —
        // otherwise downstream `ConvertFrom-Json` / `jq` chokes on the leading banner line.
        var jsonRequested = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
        if (!flags.NoBanner && !jsonRequested) PrintBanner();

        if (cleaned.Count == 0)
        {
            Console.Error.WriteLine("workshop-sentinel: missing verb. Try `workshop-sentinel help`.");
            return 2;
        }

        var verb = cleaned[0].ToLowerInvariant();
        var rest = cleaned.Skip(1).ToArray();

        return verb switch
        {
            "help" or "--help" or "-h" => HelpCommand.Run(rest),
            "list"    => ListCommand.Run(rest, ResolveSettingsStore(flags)),
            "audit"   => AuditCommand.RunAsync(rest, ResolveSettingsStore(flags)).GetAwaiter().GetResult(),
            "selfupdate" => SelfUpdateCommand.RunAsync(rest).GetAwaiter().GetResult(),
            // Stubs for not-yet-built verbs.
            "refresh" => StubVerb("refresh", "Implemented in M4 (RefreshExecutor)."),
            "doctor"  => StubVerb("doctor", "Implemented in M5 (DoctorCommand)."),
            _ => UnknownVerb(verb),
        };
    }

    /// <summary>
    /// Construct a SettingsStore honouring --config if set. Verbs use this to read user
    /// preferences (Steam path override, API key) without re-parsing argv.
    /// </summary>
    private static SettingsStore ResolveSettingsStore(GlobalFlags flags) =>
        new(flags.ConfigPath);

    private static int StubVerb(string name, string note)
    {
        Console.WriteLine($"[{name}] not yet implemented — {note}");
        return 0;
    }

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"workshop-sentinel: unknown verb '{verb}'. Try `workshop-sentinel help`.");
        return 2;
    }

    private static void PrintBanner()
    {
        Console.WriteLine($"workshop-sentinel {Program.Version} (headless)");
    }

    public sealed record GlobalFlags(bool NoBanner, string? ConfigPath);

    /// <summary>
    /// Pull global flags out of argv so verb handlers only see their own args.
    /// Supported: --no-banner, --config &lt;path&gt;.
    /// </summary>
    public static (List<string> rest, GlobalFlags flags) ExtractGlobalFlags(string[] args)
    {
        var rest = new List<string>(args.Length);
        var noBanner = false;
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--no-banner", StringComparison.OrdinalIgnoreCase))
            {
                noBanner = true;
                continue;
            }
            if (string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[++i];
                continue;
            }
            rest.Add(a);
        }

        return (rest, new GlobalFlags(noBanner, configPath));
    }
}
