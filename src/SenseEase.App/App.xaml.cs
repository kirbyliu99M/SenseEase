using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
namespace SenseEase.App;

public partial class App : Application
{
    // _luxController + LuxComfortController removed at v0.11 (Display Comfort
    // decommissioned). See CHANGELOG.md v0.11 "Removed".

    // v0.8 hotfix: single-instance guard. Pop-up sprint iter 5 introduced
    // "minimize to tray on close" — but never added a re-launch guard. So
    // every time the user closed the dashboard (which now keeps the process
    // alive in the tray) and then clicked the shortcut/exe again, a SECOND
    // process started and registered its OWN tray icon. The cycle compounds:
    // close X → relaunch → 2 trays → close X → relaunch → 3 trays …
    //
    // Named Mutex is held for the whole process lifetime; on collision we
    // try to bring the existing instance's main window forward via Win32
    // FindWindow + ShowWindow + SetForegroundWindow, then exit immediately.
    //
    // Mutex name uses the same AUMID as the shell registration so the
    // "identity" of an instance is unambiguous across diagnostics.
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = "Global\\SenseEase.App.SingleInstance";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW    = 5;

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

    // RegisterLuxController removed at v0.11 — Display Comfort decommissioned.

    /// <summary>Path Z UX iter 5: true iff the app was launched with the
    /// <c>--minimized</c> command-line arg (set by the AutoStartRegistry
    /// entry on Windows login). MainWindow.OnSourceInitialized reads this
    /// to decide whether to suppress the initial visual show — autostart
    /// goes straight to tray, manual launch shows the dashboard normally.</summary>
    public static bool StartMinimizedToTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // v0.8 hotfix: single-instance check FIRST, before AUMID/tray/diag.
        // If another SenseEase.App is already running, surface its window
        // and exit this process immediately — no MainWindow, no tray icon,
        // no engine. The existing instance keeps owning the tray entry.
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true,
                                             name: SingleInstanceMutexName,
                                             createdNew: out bool isFirstInstance);
            if (!isFirstInstance)
            {
                TrySurfaceExistingInstance();
                Shutdown(exitCode: 0);
                return;
            }
        }
        catch (Exception ex)
        {
            // If mutex creation fails (e.g. security policy), log + continue.
            // Worst case: pre-hotfix behaviour (duplicate-tray risk). Better
            // than refusing to launch at all.
            // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.SingleInstance] mutex create failed: " +
                $"{ex.GetType().Name}: {ex.Message} — continuing without guard");
        }

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
        // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
        SenseEase.Infrastructure.Real.WgcDiagLog.Append(
            $"[{DateTime.UtcNow:O}] [App.OnStartup] entry, " +
            $"build={typeof(App).Assembly.Location}, " +
            $"AUMID='{AumidString}' {aumidStatus}");

        base.OnStartup(e);

        // Head-tracking sprint M8: mirror persisted UserPreferences into
        // AppConfig static props BEFORE any MainViewModel / MainWindow
        // construction. AppConfig is the per-process source of truth read by
        // MainViewModel.CreateHeadPoseReader (factory), LightSensorProvider
        // factory, and OverlayWindow.ApplyHeadPoseToBrush / TriggerEscapeHatch
        // (per-frame). HeadTrackingEnabled is startup-bound (factory pattern);
        // FollowMode + EscapeHatchEnabled are runtime-changeable (overlay
        // reads them per frame) but we still hydrate from prefs here so the
        // first frame after launch already reflects the user's choice.
        try
        {
            Configuration.AppConfig.HeadTrackingEnabled =
                Configuration.UserPreferences.Current.HeadTrackingEnabled;
            Configuration.AppConfig.HeadTrackingFollowMode =
                string.Equals(
                    Configuration.UserPreferences.Current.HeadTrackingFollowMode,
                    "FullFollow",
                    StringComparison.OrdinalIgnoreCase)
                    ? Configuration.HeadTrackingFollowModeKind.FullFollow
                    : Configuration.HeadTrackingFollowModeKind.Balanced;
            Configuration.AppConfig.HeadTrackingEscapeHatchEnabled =
                Configuration.UserPreferences.Current.HeadTrackingEscapeHatchEnabled;
        }
        catch (Exception ex)
        {
            // Prefs load is best-effort; a malformed file or schema mismatch
            // already falls back to defaults inside Load(). This catch only
            // guards against a surprise (e.g. disk error reading the file
            // for the first time). On failure the AppConfig defaults stand
            // (head-tracking OFF / Balanced / escape hatch on).
            // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.OnStartup] HeadTracking prefs hydrate FAILED: " +
                $"{ex.GetType().Name}: {ex.Message} — using AppConfig defaults");
        }

        // Phase 4 (UI Consolidation Sprint, v0.11): one-time cleanup of the
        // orphaned Display Comfort baseline file. Display Comfort was
        // decommissioned (depth 2) at v0.11 — the DDC/CI monitor-mutation
        // pipeline was unreliable in field testing and is now removed. The
        // baseline JSON file under %LOCALAPPDATA% remained from prior
        // installs but no longer has a consumer; delete it so the user's
        // local profile directory stays clean. Hardcoded path because the
        // AppConfig.DisplayBaselinePath property was removed in the same
        // commit, and we don't want this migration to acquire a runtime
        // dependency that disappears. File.Delete is a no-op if the file
        // doesn't exist, so this is safe to run unconditionally on every
        // launch (cheap and idempotent — no migration-version bookkeeping
        // needed).
        try
        {
            string orphanedBaseline = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SenseEase", "display-baseline.json");
            if (File.Exists(orphanedBaseline))
            {
                File.Delete(orphanedBaseline);
                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[{DateTime.UtcNow:O}] [App.OnStartup] orphaned display-baseline.json deleted (v0.11 migration)");
            }
        }
        catch (Exception ex)
        {
            // Best-effort — a permission error or AV lock just leaves the
            // stale file in place; no functional impact (no consumer reads it).
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.OnStartup] display-baseline.json delete failed: " +
                $"{ex.GetType().Name}: {ex.Message} — leaving stale file in place");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception, "DispatcherUnhandledException");
            SafeShutdownActivityFilter();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
            // Only act when the runtime is terminating — non-terminating exceptions
            // on background threads must NOT trigger shutdown.
            if (args.IsTerminating) SafeShutdownActivityFilter();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Unhandled Task exceptions: log but don't terminate (mark observed).
            LogCrash(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdownActivityFilter();
        SessionEnding += (_, _) => SafeShutdownActivityFilter();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeShutdownActivityFilter();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    /// <summary>v0.8 hotfix: find the existing SenseEase MainWindow and
    /// bring it to foreground. The window title is fixed by XAML
    /// (<c>"SenseEase — Visual Comfort Dashboard"</c>) so FindWindow by
    /// title is reliable. If the window is currently hidden (close-to-tray
    /// state) or minimized, ShowWindow+SetForegroundWindow restores it.
    /// Best-effort — any failure leaves the existing instance alone.</summary>
    private static void TrySurfaceExistingInstance()
    {
        try
        {
            IntPtr hwnd = FindWindow(null, "SenseEase — Visual Comfort Dashboard");
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else                ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Idempotent, thread-safe, never throws. Shuts down
    /// <see cref="SenseEase.Infrastructure.Real.WindowsActivityFilter"/> on
    /// every exit path so its session-tracking timer doesn't leak.
    ///
    /// <para>v0.11 (UI Consolidation Sprint Phase 4): formerly
    /// <c>SafeShutdownLux</c>; the LuxComfortController shutdown branch was
    /// removed alongside the Display Comfort feature. Renamed so the call
    /// sites communicate what's still happening (ActivityFilter teardown).
    /// See CHANGELOG.md v0.11 "Removed".</para></summary>
    private static void SafeShutdownActivityFilter()
    {
        try { SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.Shutdown(); }
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
