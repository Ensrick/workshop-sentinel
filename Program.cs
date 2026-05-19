using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WorkshopSentinel.Cli;
using WorkshopSentinel.Services;

namespace WorkshopSentinel;

public static class Program
{
    public const string Version = "0.3.1";

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Best-effort cleanup of leftover self-update artifacts (a .old from a clean update,
        // or a .new from one that crashed mid-flight) before anything else touches the disk.
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            UpdateInstaller.CleanupStaleArtifacts(exePath);
        }

        if (IsHeadlessInvocation(args))
        {
            return CliRunner.Run(args);
        }

        // GUI path. Detach from the console window if we were spawned from one,
        // so a `WorkshopSentinel.exe --gui` from cmd doesn't leave a dead console
        // window behind the GUI.
        if (TryFreeConsole())
        {
            // ignored — best-effort
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    // Zero args → GUI. `--gui` (case-insensitive, any position) → GUI. Otherwise headless.
    // Matches VMBLauncher's Program.IsHeadlessInvocation rule.
    public static bool IsHeadlessInvocation(string[] args)
    {
        if (args.Length == 0) return false;
        if (args.Any(a => string.Equals(a, "--gui", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    private static bool TryFreeConsole()
    {
        try { return FreeConsole(); } catch { return false; }
    }
}
