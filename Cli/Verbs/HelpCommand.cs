using System;

namespace WorkshopSentinel.Cli.Verbs;

public static class HelpCommand
{
    public static int Run(string[] args)
    {
        Console.WriteLine(@"workshop-sentinel — audit Steam Workshop subscriptions for stale local copies.

USAGE
  workshop-sentinel <verb> [args...] [flags...]
  workshop-sentinel                 # launches the GUI (same as double-clicking)
  workshop-sentinel --gui           # forces GUI even if other args present

VERBS
  list    [--game <appid>] [--json]                       List subscribed Workshop items.
  audit   [--game <appid>] [--stale-only] [--json]        Audit local vs live; print per-item status.
  refresh <item-id> [<item-id>...] [--yes]                Force re-download (deletes local cache).
  doctor                                                  Run diagnostics (Steam path, API reachable, etc.).
  selfupdate [--yes] [--json]                             Check (and optionally install) a newer Workshop Sentinel.
  help                                                    Show this text.

GLOBAL FLAGS
  --no-banner       Suppress the version banner.
  --config <path>   Alternate settings file (default %APPDATA%\WorkshopSentinel\settings.json).
  --gui             Force GUI even with other args present.

EXIT CODES
  0  success
  1  runtime error
  2  bad usage
  3  preflight failed (Steam path missing, API unreachable, etc.)
  10 selfupdate: update available (only when --yes was NOT passed)
");
        return 0;
    }
}
