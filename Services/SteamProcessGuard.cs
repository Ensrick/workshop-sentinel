using System;
using System.Diagnostics;
using System.Linq;

namespace WorkshopSentinel.Services;

/// <summary>
/// Light-weight check for whether the Steam client is currently running on this machine.
/// Used by the refresh flow: deleting `steamapps/workshop/content/&lt;appid&gt;/&lt;itemid&gt;/`
/// while Steam holds a handle on a file inside it tends to throw IOException. We don't
/// auto-kill — Steam might be downloading something else or holding a chat connection —
/// we just surface a warning so the user can close it themselves.
/// </summary>
public sealed class SteamProcessGuard
{
    private readonly Func<string[]> _runningProcessNames;

    public SteamProcessGuard() : this(GetRunningProcessNames) { }

    /// <summary>Test seam — inject a synthetic process name list.</summary>
    public SteamProcessGuard(Func<string[]> runningProcessNames)
    {
        _runningProcessNames = runningProcessNames;
    }

    public bool IsSteamRunning()
    {
        try
        {
            var names = _runningProcessNames();
            return names.Any(n => string.Equals(n, "steam", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Permission errors enumerating processes shouldn't block a refresh attempt.
            return false;
        }
    }

    private static string[] GetRunningProcessNames()
    {
        return Process.GetProcesses().Select(p =>
        {
            try { return p.ProcessName; }
            catch { return string.Empty; }
        }).ToArray();
    }
}
