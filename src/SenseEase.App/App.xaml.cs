using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SenseEase.App.Comfort;

namespace SenseEase.App;

public partial class App : Application
{
    private static LuxComfortController? _luxController;

    /// <summary>Patch 8 (sub-bug #2): Win32 Shell_NotifyIcon associates tray
    /// entries with the process's AppUserModelID (AUMID). Without an explicit
    /// AUMID, Windows shell falls back to the host-process identity, which on
    /// dev builds invoked via `dotnet test` had been showing as "testhost".
    /// Calling this with our app-specific AUMID at the earliest point in
    /// startup pins the shell-integration identity to SenseEase.</summary>
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    private const string AumidString = "SenseEase.App.Desktop";

    // Crash log: %LOCALAPPDATA%\SenseEase\crash.log — surface XAML binding errors,
    // background-thread exceptions, etc. without forcing the user to dig in Event Viewer.
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "crash.log");

    /// <summary>Called by MainViewModel.WireLuxComfort so SafeShutdownLux can reach
    /// the controller from any thread without touching WPF objects.</summary>
    public static void RegisterLuxController(LuxComfortController controller) =>
        _luxController = controller;

    /// <summary>Path Z UX iter 5: true iff the app was launched with the
    /// <c>--minimized</c> command-line arg (set by the AutoStartRegistry
    /// entry on Windows login). MainWindow.OnSourceInitialized reads this
    /// to decide whether to suppress the initial visual show — autostart
    /// goes straight to tray, manual launch shows the dashboard normally.</summary>
    public static bool StartMinimizedToTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Path Z UX iter 5: detect autostart-from-Windows-login. The
        // AutoStartRegistry HKCU entry passes --minimized so the app
        // boots straight to the tray instead of flashing the dashboard.
        // Parsed early so MainWindow's constructor / OnSourceInitialized
        // can read the static flag.
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, Configuration.AutoStartRegistry.MinimizedArg, StringComparison.OrdinalIgnoreCase))
            {
                StartMinimizedToTray = true;
                break;
            }
        }

        // Patch 8 (sub-bug #2): set AUMID BEFORE anything registers with the
        // shell (tray icons, taskbar grouping, toast notifications). Once a
        // process has registered any shell-integrated UI without an AUMID,
        // changing it later is ineffective — Windows caches the original
        // identity. This MUST be the very first line of OnStartup.
        string aumidStatus;
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AumidString);
            aumidStatus = "set OK";
        }
        catch (Exception ex)
        {
            aumidStatus = $"FAILED: {ex.GetType().Name}: {ex.Message}";
        }

        // overlay-bar-redesign diag-3: earliest-possible log line. Runs
        // BEFORE base.OnStartup(e), BEFORE any MainWindow construction.
        // If a user-pasted wgc_diag.txt does NOT contain [App.OnStartup]
        // for a given app run, the user is running a stale binary that
        // pre-dates this commit — not a behavioural bug. The assembly
        // Location string surfaces WHICH binary is loaded so build path
        // mismatches (publish/debug confusion) are visible. Patch 8
        // augments the log with AUMID assignment outcome so we can
        // distinguish "AUMID never set" from "AUMID set but shell cached
        // the prior testhost entry" in user-pasted diagnostics.
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "wgc_diag.txt"),
                $"[{DateTime.UtcNow:O}] [App.OnStartup] entry, " +
                $"build={typeof(App).Assembly.Location}, " +
                $"AUMID='{AumidString}' {aumidStatus}\n");
        }
        catch { /* fire-and-forget */ }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception, "DispatcherUnhandledException");
            SafeShutdownLux();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
            // Only restore when the runtime is terminating — non-terminating exceptions
            // on background threads must NOT shut the controller down or the display is
            // stuck at baseline while the app continues to run.
            if (args.IsTerminating) SafeShutdownLux();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Unhandled Task exceptions: log but don't terminate (mark observed).
            LogCrash(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdownLux();
        SessionEnding += (_, _) => SafeShutdownLux();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeShutdownLux();
        base.OnExit(e);
    }

    /// <summary>Idempotent, thread-safe, never throws. The user's hard requirement
    /// "restore on close" routes through here on every exit path.</summary>
    private static void SafeShutdownLux()
    {
        try { SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.Shutdown(); } catch { }
        try { _luxController?.Shutdown(); }
        catch { /* never crash on shutdown */ }
    }

    /// <summary>Appends one timestamped record per crash. Best-effort: any IO failure
    /// here is silently swallowed — we are already in a crash path.</summary>
    private static void LogCrash(Exception? ex, string source)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] =====" + Environment.NewLine
                + ex.ToString() + Environment.NewLine + Environment.NewLine);
        }
        catch { /* never crash inside the crash handler */ }
    }
}
