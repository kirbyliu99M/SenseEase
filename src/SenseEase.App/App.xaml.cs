using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    // H30/H31: per-session ("Local\") mutex, NOT cross-user ("Global\"). A
    // Global mutex name is shared across every interactive session on the
    // machine, so a second user logging in (RDP / fast-user-switching) would
    // have their first SenseEase launch silently treated as a duplicate of
    // ANOTHER user's instance — and the surface attempt would target a window
    // in a different session it can't even see. "Local\" scopes the guard to
    // the current login session, which is the correct boundary for a
    // single-instance-per-desktop tray app.
    private const string SingleInstanceMutexName = "Local\\SenseEase.App.SingleInstance";

    /// <summary>H32 integrity gate: set true when
    /// <see cref="SenseEase.Infrastructure.Storage.IntegrityVerifier"/> reports
    /// a bundled binary failed its SHA-256 manifest check at startup. When true
    /// the app degrades — the head-tracking sidecar and the local LLM are NOT
    /// launched — rather than running tampered executables. Read by the launch
    /// sites (parity with how <see cref="StartMinimizedToTray"/> is consumed by
    /// MainWindow). In Alpha-1 the manifest is absent so Verify always returns
    /// true and this stays false (dormant gate; activates with the packaging
    /// manifest).</summary>
    public static bool IntegrityFailed { get; private set; }

    /// <summary>H32: user-facing degrade message set when
    /// <see cref="IntegrityFailed"/> is raised. Mirrors the existing
    /// LoadError-style surface (e.g. MistralChatbotService.LoadError) so the UI
    /// can show a non-fatal banner instead of hard-crashing. Null when no
    /// integrity failure occurred.</summary>
    public static string? LoadError { get; private set; }

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
            // H30/H31: construct UNOWNED (initiallyOwned:false), then claim
            // ownership explicitly via WaitOne(0). Owning-at-construction
            // conflates "I created the kernel object" with "I hold the lock",
            // and worse, an owned mutex that is never released because the
            // owning thread is torn down raises AbandonedMutexException on the
            // NEXT waiter — which the prior code never handled. Separating
            // creation from acquisition lets us catch the abandoned case
            // distinctly below.
            _singleInstanceMutex = new Mutex(initiallyOwned: false,
                                             name: SingleInstanceMutexName);

            bool weAreFirst;
            try
            {
                // WaitOne(0): non-blocking. True  → no other live instance held
                // the mutex, we now own it → we are the first instance. False →
                // another instance currently owns it → we are the second.
                weAreFirst = _singleInstanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // H30/H31: a prior instance acquired the mutex and exited
                // WITHOUT releasing it (hard kill, crash before OnExit). The
                // wait that observes this STILL transfers ownership to us — so
                // we ARE the first (and only) live instance. Treat as such and
                // proceed to launch normally rather than mis-surfacing toward a
                // dead window.
                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[{DateTime.UtcNow:O}] [App.SingleInstance] abandoned mutex reclaimed " +
                    "(prior instance exited without release) — proceeding as first instance");
                weAreFirst = true;
            }

            if (!weAreFirst)
            {
                // Second instance: try to surface the existing window. If we
                // can't find it, leave a diagnostic so a "clicking the icon
                // does nothing" field report is explainable (e.g. the prior
                // instance is alive but its window title changed, or it is
                // mid-startup before the window exists).
                if (!TrySurfaceExistingInstance())
                {
                    string diag =
                        $"[{DateTime.UtcNow:O}] [App.SingleInstance] second instance: " +
                        "existing SenseEase window not found to surface — exiting anyway";
                    SenseEase.Infrastructure.Real.WgcDiagLog.Append(diag);
                    LogStartupDiagnostic(diag);
                }
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

        // H32 integrity gate: verify the bundled executables the app will
        // launch (head-tracking sidecar + local LLM server) against the
        // install manifest BEFORE any of them are spawned. Runs immediately
        // after the single-instance check so a tampered binary never reaches
        // Process.Start. Alpha-1 builds ship without install.manifest.json, so
        // IntegrityVerifier.Verify returns true here (logs "integrity check
        // skipped: no manifest") and this is a no-op — the gate activates only
        // once the packaging phase emits a manifest. On a real FALSE result we
        // degrade (set LoadError + suppress sidecar/LLM launch), NOT crash.
        try
        {
            // Pass the FULL nested paths the app actually launches: the bundled
            // exes ship under tools/.../head_tracker/ and runtimes/.../vulkan/,
            // NOT next to BaseDirectory. IntegrityVerifier hashes the rooted path
            // and keys the manifest by Path.GetFileName, so a full path verifies
            // the real file while still matching the bare-name manifest entry.
            // (A bare "head_tracker.exe"/"llama-server.exe" resolved to
            // baseDir\<name>, which the packaged build does not contain — the gate
            // would fail closed on every launch once a manifest ships.)
            string sidecarExePath   = Configuration.AppConfig.SidecarExePath;
            string llmServerExePath =
                SenseEase.Infrastructure.Real.MistralChatbotService.ResolveServerExePath();

            bool sidecarOk = SenseEase.Infrastructure.Storage.IntegrityVerifier.Verify(sidecarExePath);
            bool llmOk     = SenseEase.Infrastructure.Storage.IntegrityVerifier.Verify(llmServerExePath);

            if (!sidecarOk || !llmOk)
            {
                IntegrityFailed = true;
                LoadError = "Installation integrity check failed; please reinstall.";

                // Refuse to launch the sidecar: disabling head-tracking makes
                // CreateHeadPoseReader return the no-op DefaultHeadPoseReader,
                // so no head_tracker.exe is ever spawned this session. The LLM
                // launch sites honor the IntegrityFailed flag (parity with the
                // StartMinimizedToTray static read elsewhere).
                Configuration.AppConfig.HeadTrackingEnabled = false;

                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[{DateTime.UtcNow:O}] [App.Integrity] FAILED — sidecarOk={sidecarOk}, " +
                    $"llmOk={llmOk}; suppressing sidecar + LLM launch, surfacing LoadError " +
                    "(degraded, not crashed)");
            }
        }
        catch (Exception ex)
        {
            // The verifier is pure and does not throw on the absent-manifest
            // path; any surprise (e.g. an unexpected IO fault) is best-effort
            // logged and we DO NOT block launch on it — same degrade-not-crash
            // posture as the rest of OnStartup.
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.Integrity] verify threw: " +
                $"{ex.GetType().Name}: {ex.Message} — continuing without integrity gate");
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
            // Taskbar-icon fix (Kirby — dead across every icon change): an explicit
            // AUMID with NO installed shortcut registering an icon for it leaves the
            // taskbar button BLANK, while Window.Icon still paints the correct
            // title-bar / Alt-Tab icon (exactly the observed symptom). Dropping the
            // explicit AUMID lets the taskbar use the EXE's default identity + embedded
            // ApplicationIcon, which renders. The tray icon is set explicitly
            // (TrayIconService) so it is unaffected; the only thing the AUMID bought was
            // a cosmetic shell identity under `dotnet test` (testhost) no end user sees.
            // (Disabled) SetCurrentProcessExplicitAppUserModelID(AumidString);
            aumidStatus = "not set (taskbar-icon fix)";
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

        // Live EN / 繁體中文 language toggle: hydrate the process-wide language from
        // persisted prefs BEFORE base.OnStartup parses MainWindow, so localized surfaces
        // ({loc:T} bindings + language-aware tier converters) render in the chosen
        // language from the first frame instead of flashing the default then re-picking.
        SenseEase.App.Localization.LocalizationManager.Current.Language =
            SenseEase.App.Localization.LocalizationManager.Parse(
                SenseEase.App.Configuration.UserPreferences.Current.LanguageModeRaw);

        base.OnStartup(e);

        // Follow the Windows light/dark system setting. ThemeManager registers
        // a Window class-handler so every window merges the dark palette
        // view-locally when the OS is in dark mode (and removes it on a flip
        // back to light), mirroring SenseEaseTheme.xaml's "never App-level"
        // rule so the bare-Application smoke tests — which never reach this
        // path — keep their light defaults. Wrapped in try/catch for the same
        // degrade-not-crash posture as the rest of OnStartup: a registry/IO
        // failure inside the manager leaves the light default and never throws.
        try
        {
            SenseEase.App.Theme.ThemeManager.Initialize();
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.OnStartup] ThemeManager.Initialize FAILED: " +
                $"{ex.GetType().Name}: {ex.Message} — staying on light default");
        }

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
            // H32: if the integrity gate failed above, the sidecar must stay
            // suppressed regardless of the user's saved preference — otherwise
            // this hydration would silently re-enable head-tracking (and the
            // head_tracker.exe spawn) after we explicitly disabled it. Only
            // mirror the saved preference when integrity passed.
            if (!IntegrityFailed)
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
    /// Best-effort — any failure leaves the existing instance alone.
    ///
    /// <para>H30/H31: returns <c>true</c> when the existing window was found
    /// (and a surface attempt was made), <c>false</c> when no such window
    /// exists or an error occurred. The second-instance path uses the result
    /// to decide whether to leave a "couldn't find existing window" diagnostic
    /// before exiting.</para></summary>
    private static bool TrySurfaceExistingInstance()
    {
        try
        {
            IntPtr hwnd = FindWindow(null, "SenseEase — Visual Comfort Dashboard");
            if (hwnd == IntPtr.Zero) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            else                ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
            return true;
        }
        catch { /* best-effort */ return false; }
    }

    /// <summary>H30/H31: append a one-line startup diagnostic to crash.log.
    /// Used for the second-instance "existing window not found" case so the
    /// failure is visible in the durable crash.log even if the user never
    /// captures the %TEMP% wgc_diag.txt. Best-effort and never throws — runs
    /// the same PII redaction + append pattern as <see cref="LogCrash"/>.</summary>
    private static void LogStartupDiagnostic(string line)
    {
        try
        {
            string redacted = UserPathRedaction.Replace(line, @"\Users\<redacted>$1");
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [App.SingleInstance] =====" + Environment.NewLine
                + redacted + Environment.NewLine + Environment.NewLine);
        }
        catch { /* never crash inside a diagnostic path */ }
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

    // H19: runtime PII guard for crash.log. The Directory.Build.props PathMap
    // normalizes compiler-embedded source paths at build time, but stack
    // traces can still surface absolute paths captured at runtime (e.g. from
    // third-party/precompiled assemblies whose PDBs we don't control, or from
    // exception messages that interpolate a file path under the user profile).
    // Any "...\Users\<name>\..." segment leaks the OS account name, so we
    // redact the <name> component to a placeholder before writing. Case-
    // insensitive; the captured group preserves the trailing separator.
    private static readonly Regex UserPathRedaction = new(
        @"\\Users\\[^\\/]+(\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Appends one timestamped record per crash. Best-effort: any IO failure
    /// here is silently swallowed — we are already in a crash path.</summary>
    private static void LogCrash(Exception? ex, string source)
    {
        if (ex == null) return;
        try
        {
            // H19: redact any "\Users\<name>\" segment before persisting so the
            // OS account name never lands in the shipped crash.log.
            string redacted = UserPathRedaction.Replace(ex.ToString(), @"\Users\<redacted>$1");
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] =====" + Environment.NewLine
                + redacted + Environment.NewLine + Environment.NewLine);
        }
        catch { /* never crash inside the crash handler */ }
    }
}
