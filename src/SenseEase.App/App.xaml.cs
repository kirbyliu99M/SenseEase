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
    // H30/H31: per-session ("Local\") mutex, NOT cross-user ("Global\"). A
    // Global mutex name is shared across every interactive session on the
    // machine, so a second user logging in (RDP / fast-user-switching) would
    // have their first SenseEase launch silently treated as a duplicate of
    // ANOTHER user's instance — and the surface attempt would target a window
    // in a different session it can't even see. "Local\" scopes the guard to
    // the current login session, which is the correct boundary for a
    // single-instance-per-desktop tray app.
    private const string SingleInstanceMutexName = "Local\\SenseEase.App.SingleInstance";

    // Dashboard-ghost fix (2026-06-22): a SECOND launch must NOT surface the console window. The dashboard was
    // dismantled to a HEADLESS host, so the old "ShowWindow + SetForegroundWindow on the MainWindow" path
    // brought up a BLANK window — the "dashboard ghost" (worsened by the localized title, which made the
    // by-title FindWindow EN-only). Instead the second instance Set()s this named auto-reset event; the first
    // instance's background listener wakes and reveals the SHELL entry point (popup / edge sidebar) on the UI
    // thread — the same surface the tray left-click reveals. Language-independent; never shows the headless console.
    private static EventWaitHandle? _revealSignal;
    private static Thread? _revealSignalListener;
    private const string RevealSignalName = "Local\\SenseEase.App.RevealShell";

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
    /// LoadError-style surface (e.g. LocalLlmChatbotService.LoadError) so the UI
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
        // EXIT-02 (CW-11): register every crash / lifecycle hook as the FIRST
        // statements of OnStartup — BEFORE the mutex / integrity / localization /
        // prefs work below. Those are exactly the steps where a Trend-Micro
        // transient file-lock bites at login-autostart; with the handlers wired
        // first, a throw ANYWHERE in startup still leaves a crash.log record
        // instead of dying with zero evidence. The handlers are self-contained
        // statics (LogCrash / SafeShutdownActivityFilter / the crash-flush hook
        // accessor) that never touch instance state, so moving them ahead of the
        // rest of startup is safe — none of them depend on anything initialized
        // later in this method.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception, "DispatcherUnhandledException");
            // EXIT-05: best-effort flush of the in-memory behaviour aggregate so a
            // UI-thread crash doesn't lose the ≤60 s tail. Static-safe (the VM may
            // not exist yet) and never throws — see TryFlushBehaviorForCrash.
            TryFlushBehaviorForCrash();
            SafeShutdownActivityFilter();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
            // Only act when the runtime is terminating — non-terminating exceptions
            // on background threads must NOT trigger shutdown.
            if (args.IsTerminating)
            {
                // EXIT-05: terminating fault — flush the behaviour tail before teardown.
                TryFlushBehaviorForCrash();
                SafeShutdownActivityFilter();
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Unhandled Task exceptions: log but don't terminate (mark observed).
            LogCrash(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdownActivityFilter();
        SessionEnding += (_, _) => SafeShutdownActivityFilter();

        // B0 deferred seam: push the App-layer data-root override down into
        // Infrastructure so LocalLlmChatbotService's model_trace.jsonl routes through
        // the SAME AppConfig.DataRoot the App-layer paths use (Infrastructure must
        // NOT reference the App assembly — same cross-boundary pattern as
        // ChatbotUiLanguage / LlamaSessionKey). The lambda reads AppConfig.DataRoot
        // at call time, so production resolves the real per-user folder and the test
        // suite's per-class override redirects the trace off the developer's data.
        SenseEase.Infrastructure.Real.SenseEaseDataRoot.Provider =
            () => Configuration.AppConfig.DataRoot;

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

            // We are the first (and only) live instance. Listen for a later launch's reveal signal so that a
            // second launch brings the SHELL forward (popup / edge sidebar) instead of surfacing the headless
            // console window — the dashboard-ghost fix.
            StartRevealSignalListener();
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
                SenseEase.Infrastructure.Real.LocalLlmChatbotService.ResolveServerExePath();

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

        // PKG-06: full manifest sweep — verify EVERY listed file (the loaded
        // llama*.dll / mtmd.dll / libomp*.dll, the PyInstaller _internal/ tree, the
        // face_landmarker.task model), not just the two spawn-gated exe stubs the
        // synchronous gate above covers. Runs ONCE, on a background task, because
        // hashing ~350 MB must never block the UI thread / first frame.
        //
        // DIAGNOSTIC this wave (Alpha-1 posture per SECURITY.md / SEC-03): a sweep
        // failure is logged to wgc_diag (redacted) but does NOT raise the
        // 'please reinstall' suppression — that stays gated to the two synchronous
        // exe checks above exactly as today, so a manifest-vs-_internal mismatch on a
        // dev/alpha build can't strand the app. Once a manifest actually ships and the
        // packaging phase lands, this is where a stricter posture would hook in. On
        // current builds (no manifest) VerifyManifestSweep returns an empty list and
        // logs "integrity sweep skipped: no manifest" — a no-op. Self-contained +
        // never-throws (the task body is fully wrapped) so it can run before the VM /
        // window exist without any ordering dependency.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var sweep = SenseEase.Infrastructure.Storage.IntegrityVerifier.VerifyManifestSweep();
                foreach (var entry in sweep)
                {
                    if (entry.Status == SenseEase.Infrastructure.Storage.ManifestEntryStatus.Ok)
                        continue;

                    // SEC-01: the Detail can carry a resolved "...\Users\<name>\..."
                    // path, so redact before it reaches wgc_diag (handbook-zipped).
                    string detail = SenseEase.Infrastructure.Storage.PathRedactor.Redact(entry.Detail);
                    SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                        $"[{DateTime.UtcNow:O}] [App.Integrity.Sweep] {entry.Status}: " +
                        $"{entry.Key} — {detail} (diagnostic only; no reinstall surface this wave)");
                }
            }
            catch (Exception ex)
            {
                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[{DateTime.UtcNow:O}] [App.Integrity.Sweep] threw: " +
                    $"{ex.GetType().Name}: {ex.Message} — sweep skipped (non-blocking)");
            }
        });

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

        // UI-RESHAPE: the dashboard console is no longer the launch surface — it stays the hidden
        // engine/host. ALWAYS boot hidden-to-tray; the always-on edge sidebar (ShellController) is
        // the entry point, and the console is reachable only on demand (tray / "Full settings").
        StartMinimizedToTray = true;

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
        // factory, and OverlayWindow.ApplyHeadPoseToBrush (per-frame).
        // HeadTrackingEnabled is startup-bound (factory pattern); FollowMode
        // is runtime-changeable (the overlay reads it per frame) but we still
        // hydrate from prefs here so the first frame after launch already
        // reflects the user's choice. (D-10: the escape-hatch flag was retired.)
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
            // 2026-06-18 (user): follow mode is FIXED to FullFollow — the Balanced option + its
            // Settings radios were removed (no user choice). Force it regardless of the saved pref.
            Configuration.AppConfig.HeadTrackingFollowMode = Configuration.HeadTrackingFollowModeKind.FullFollow;
            // Beta-1 D-16: mirror the per-app attribution consent (default OFF,
            // STRICTLY opt-in) onto AppConfig BEFORE the MainViewModel ctor reads
            // it to seed WindowsActivityFilter.Instance.PerAppAttributionEnabled.
            // NOT integrity-gated — it spawns no native process; it only governs
            // whether the foreground app name (on-device only, D-16) is stamped
            // onto data-points. Runtime-changeable thereafter via Settings.
            Configuration.AppConfig.PerAppAttributionEnabled =
                Configuration.UserPreferences.Current.PerAppAttributionEnabled;
            // R3 additional #3: keep the Windows auto-start (HKCU…\Run) entry in sync with the pref
            // on EVERY launch — so the new default-ON registers on a fresh install, and a
            // toggled-off pref clears any stale entry. Idempotent; best-effort.
            try { Configuration.AutoStartRegistry.SetEnabled(Configuration.UserPreferences.Current.RunAtWindowsStartup); } catch { }
            // D-10 (2026-06-11): the escape-hatch hydrate was removed with the
            // feature. AppConfig.HeadTrackingEscapeHatchEnabled no longer exists
            // (FEAT-06 cancelled — the FOV mask is never weakened), so there is
            // nothing to mirror from prefs here.

            // Diagnostic: record the RESOLVED head-tracking state at startup so a field
            // report ("head-tracking isn't working") is self-explaining in the next diag —
            // it pins whether the runtime flag matched the saved preference, or a transient
            // prefs-load miss (now retried in AtomicJsonWriter.TryRead) reset it to default.
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.OnStartup] head-tracking hydrate: " +
                $"AppConfig.HeadTrackingEnabled={Configuration.AppConfig.HeadTrackingEnabled} " +
                $"prefs.HeadTrackingEnabled={Configuration.UserPreferences.Current.HeadTrackingEnabled} " +
                $"followMode={Configuration.AppConfig.HeadTrackingFollowMode} " +
                $"integrityFailed={IntegrityFailed}");
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

        // FEAT-13/DEAD-07: one-time startup migration delete for the orphaned
        // calibration_history.jsonl. The calibration subsystem (CalibrationChatbotService
        // + its writer) was removed with D-13; the file is content-bearing PII (calibration
        // Q&A, may include health details) that survived every in-app Settings clear on
        // upgraded installs. Delete it so the user's data folder stays clean on all
        // launch paths — even for users who never open Settings. Mirrors the existing
        // display-baseline.json migration pattern exactly: best-effort, logged, never throws.
        // T4 (Settings clear-all) also deletes it; both are correct and idempotent.
        try
        {
            string orphanedCalibrationHistory = Configuration.AppConfig.CalibrationHistoryPath;
            if (File.Exists(orphanedCalibrationHistory))
            {
                File.Delete(orphanedCalibrationHistory);
                SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                    $"[{DateTime.UtcNow:O}] [App.OnStartup] orphaned calibration_history.jsonl deleted (FEAT-13/DEAD-07 migration)");
            }
        }
        catch (Exception ex)
        {
            // Best-effort — a permission error or AV lock just leaves the
            // stale file in place; no functional impact (no consumer reads it).
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.OnStartup] calibration_history.jsonl delete failed: " +
                $"{ex.GetType().Name}: {ex.Message} — leaving stale file in place");
        }

        // UM4: kick off the silent retrospective user-model analysis. App.xaml uses
        // StartupUri, so Application.MainWindow is still NULL at the end of OnStartup —
        // the window is created/shown AFTER OnStartup returns. Calling the kickoff here
        // was a permanent no-op (CW-4). Instead defer the kickoff to AFTER the StartupUri
        // window exists, via two redundant one-shot hooks that both funnel through the
        // idempotent OnFirstActivatedStartUserModelAnalysis:
        //   • Activated fires once the app becomes foreground (normal manual launch);
        //   • a dispatcher ApplicationIdle callback also covers the autostart
        //     --minimized case, where the window starts hidden in the tray and may
        //     never raise Activated — ApplicationIdle still runs once the message pump
        //     settles, by which point MainWindow.DataContext is the MainViewModel.
        // The handler self-unsubscribes Activated and the VM entry is itself idempotent
        // (CTS guard), so whichever hook fires first wins and the second is a no-op. Kept
        // in App (not the VM) so it NEVER runs under unit tests, which construct
        // MainViewModel / MainWindow directly without an Application activation.
        Activated += OnFirstActivatedStartUserModelAnalysis;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => OnFirstActivatedStartUserModelAnalysis(this, EventArgs.Empty)));
    }

    /// <summary>CW-4: one-shot kickoff that starts the UM4 retrospective analysis once the
    /// MainWindow exists (StartupUri creates it AFTER OnStartup returns, so the old
    /// end-of-OnStartup call saw a null MainWindow and never fired). Reached from two
    /// redundant hooks (<see cref="Application.Activated"/> + a dispatcher ApplicationIdle
    /// callback); self-unsubscribes Activated and the VM entry is idempotent, so it runs
    /// exactly once per app run regardless of which hook fires first. The VM owns the shared
    /// habituation-profile store, so the analyser's writes stay atomic against the engine's
    /// persist (W2.0-C). Best-effort: never throws. Internal so a regression test can pin
    /// the wiring seam without spinning a real WPF Application (its absence means a
    /// regression back to the inert null-MainWindow inline pattern).</summary>
    internal void OnFirstActivatedStartUserModelAnalysis(object? sender, EventArgs e)
    {
        Activated -= OnFirstActivatedStartUserModelAnalysis; // one-shot per app run
        try
        {
            (MainWindow?.DataContext as SenseEase.App.ViewModels.MainViewModel)
                ?.StartBackgroundUserModelAnalysis();
        }
        catch { /* background analysis is best-effort */ }

        // UI-RESHAPE: stand up the coexistence shell (edge sidebar + popup) once the StartupUri
        // MainWindow + its MainViewModel exist. Reuses this proven dual-hook (Activated + the
        // ApplicationIdle BeginInvoke below) so it also fires on the hidden-to-tray boot.
        StartShellIfNeeded();
    }

    // UI-RESHAPE: the coexistence shell controller (App-lifetime; keeps the new surfaces alive).
    private SenseEase.App.Services.ShellController? _shell;

    // TASK #7 (UI-RESHAPE): the system-wide reveal/toggle hotkey (App-lifetime). Created once the
    // shell exists (StartShellIfNeeded); Pressed → ShellController.ToggleGlassPopupBar on the UI
    // thread. Re-registered after a Settings change via ReRegisterRevealHotkey. Disposed in OnExit.
    private SenseEase.App.Services.GlobalHotkeyService? _revealHotkey;

    /// <summary>Idempotent one-shot: create + start the <see cref="SenseEase.App.Services.ShellController"/>
    /// once the StartupUri MainWindow and its <see cref="ViewModels.MainViewModel"/> are available.
    /// Best-effort: a failure here must never block launch (the console host still works).</summary>
    private void StartShellIfNeeded()
    {
        if (_shell is not null) return;                                   // already started
        if (MainWindow is not SenseEase.App.MainWindow win) return;       // window not ready yet
        if (win.DataContext is not SenseEase.App.ViewModels.MainViewModel vm) return;
        try
        {
            _shell = new SenseEase.App.Services.ShellController(win, vm);
            _shell.Start();
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.StartShell] failed: {ex.GetType().Name}: {ex.Message}");
        }

        // TASK #7: stand up the system-wide reveal/toggle hotkey once the shell exists. Pressed
        // marshals to the shell's dispatcher and toggles the glass popup bar. Fail-soft by
        // construction — Register never throws; a missing/contended key just leaves no hotkey
        // (the edge-sidebar click still reveals the bar), so this never blocks launch.
        try
        {
            if (_revealHotkey is null)
            {
                _revealHotkey = new SenseEase.App.Services.GlobalHotkeyService();
                _revealHotkey.Pressed += OnRevealHotkeyPressed;
                _revealHotkey.Register(Configuration.UserPreferences.Current.RevealHotkey);
            }
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.StartShell] reveal-hotkey wiring failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>TASK #7: the global reveal hotkey fired. Marshal to the shell's dispatcher and
    /// toggle the glass popup bar (lazily created). Best-effort — a fault here (e.g. the shell
    /// not yet built) is swallowed so a stray WM_HOTKEY never reaches an unhandled handler.</summary>
    private void OnRevealHotkeyPressed(object? sender, EventArgs e)
    {
        try
        {
            // ShellController owns the popup; its dispatcher is the UI thread. The HwndSource hook
            // already runs on the UI thread in normal operation, but marshal defensively so the
            // toggle is always on the right thread regardless of where the message pump delivers it.
            Dispatcher.BeginInvoke(new Action(() => _shell?.ToggleGlassPopupBar()));
        }
        catch { /* best-effort — a reveal-hotkey fault must never crash the app */ }
    }

    /// <summary>TASK #7: re-register the reveal hotkey from the current
    /// <see cref="Configuration.UserPreferences"/> value. Called by Settings (via the
    /// <c>SetRevealHotkey</c> host delegate) AFTER it persists a new gesture, so the live binding
    /// matches the saved pref without a restart. Idempotent + fail-soft: a no-shell / no-service
    /// state is a no-op, and the underlying Register never throws. Marshals to the UI thread
    /// because RegisterHotKey must run on the thread that owns the message-sink window.</summary>
    internal void ReRegisterRevealHotkey()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ReRegisterRevealHotkey));
                return;
            }
            _revealHotkey?.Register(Configuration.UserPreferences.Current.RevealHotkey);
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.ReRegisterRevealHotkey] failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Reveal the new edge/popup panel — the tray single-click entry point (replaces the
    /// legacy notification card). Idempotent (a no-op if already shown); marshals to the UI thread
    /// because it creates/shows the popup window.</summary>
    internal void RevealShellPanel()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RevealShellPanel)); return; }
        _shell?.RevealFromTray(); // tray reveal: cursor is far → keep it open ~10 s (vs side-panel 5 s)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeShutdownActivityFilter();
        // TASK #7: release the system-wide hotkey + dispose the message-sink window so the
        // registration doesn't outlive the process. Best-effort — never throws on teardown.
        try
        {
            if (_revealHotkey is not null)
            {
                _revealHotkey.Pressed -= OnRevealHotkeyPressed;
                _revealHotkey.Unregister();
                _revealHotkey.Dispose();
            }
        }
        catch { /* best-effort */ }
        _revealHotkey = null;
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        _singleInstanceMutex = null;
        // Ghost fix: dispose the reveal event so its background listener (WaitOne) wakes with
        // ObjectDisposedException and returns instead of lingering.
        try { _revealSignal?.Dispose(); } catch { }
        _revealSignal = null;
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
        // Dashboard-ghost fix (2026-06-22): signal the running instance to bring its SHELL (popup / edge
        // sidebar) forward, NOT the old ShowWindow on the now-headless console window — that surfaced a blank
        // "dashboard ghost", and its by-title FindWindow was EN-only once the title localized. Set() the
        // cross-process named event the first instance's StartRevealSignalListener waits on. Returns true when
        // the running instance was signalled, false if its listener isn't up yet (still mid-startup).
        try
        {
            if (EventWaitHandle.TryOpenExisting(RevealSignalName, out var ev))
            {
                using (ev) ev.Set();
                return true;
            }
            return false;
        }
        catch { /* best-effort */ return false; }
    }

    /// <summary>First-instance side of the dashboard-ghost fix: own the named reveal event + a background
    /// listener. When a later launch <c>Set()</c>s it, marshal to the UI thread and reveal the shell entry
    /// point (the same surface the tray left-click shows) — never the headless console window. The listener is
    /// a background thread (dies with the process); disposing <see cref="_revealSignal"/> on exit wakes it to
    /// return. Best-effort — a setup failure just means a second launch can't reveal (it still won't ghost).</summary>
    private void StartRevealSignalListener()
    {
        try
        {
            _revealSignal = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: RevealSignalName);
            _revealSignalListener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _revealSignal.WaitOne();
                        Dispatcher?.BeginInvoke(new Action(RevealShellPanel));
                    }
                    catch (ObjectDisposedException) { return; }   // app shutting down → the event was disposed
                    catch { /* transient — keep listening */ }
                }
            })
            { IsBackground = true, Name = "SenseEase.RevealSignalListener" };
            _revealSignalListener.Start();
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [App.SingleInstance] reveal-listener setup failed: {ex.GetType().Name}: {ex.Message}");
        }
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
            // SEC-01: single source of truth — delegate to the shared PathRedactor
            // (the inline H19 regex this used to carry was lifted there verbatim).
            string redacted = SenseEase.Infrastructure.Storage.PathRedactor.Redact(line);
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

    /// <summary>EXIT-05 (CW-11): best-effort, static-safe flush of the in-memory daily
    /// behaviour aggregate on a crash path, via the hook G2 exposed on MainViewModel
    /// (<see cref="SenseEase.App.ViewModels.MainViewModel.TryFlushBehaviorAggregateForCrash"/>).
    /// Called from the DispatcherUnhandledException and the IsTerminating
    /// AppDomain.UnhandledException handlers so a hard termination doesn't lose the
    /// ≤60 s behaviour tail accumulated since the last durability heartbeat.
    ///
    /// <para><b>Static-safe.</b> The crash can fire BEFORE StartupUri creates the window
    /// (so <c>Current</c> / <c>MainWindow</c> / its <c>DataContext</c> may all be null) or
    /// off any thread; every dereference is null-conditional and the whole body is wrapped
    /// so a crash handler NEVER throws a second exception. When no VM exists yet there is
    /// simply nothing to flush. The VM hook is itself idempotent (idempotent-by-date
    /// writer) and Q4.5-safe — it writes telemetry only, never engine/habituation state,
    /// so it can never clear the habituation offset.</para></summary>
    private static void TryFlushBehaviorForCrash()
    {
        try
        {
            (Current?.MainWindow?.DataContext as SenseEase.App.ViewModels.MainViewModel)
                ?.TryFlushBehaviorAggregateForCrash();
        }
        catch { /* never throw a second exception from inside a crash handler */ }
    }

    // SEC-01: the H19 runtime PII guard (the inline "\Users\<name>\" → "<redacted>"
    // regex) was lifted verbatim into SenseEase.Infrastructure.Storage.PathRedactor
    // so crash.log, wgc_diag.txt, and llama-server.log all redact through ONE source
    // of truth. App.xaml.cs now calls PathRedactor.Redact(...) at its two crash-log
    // write sites (LogCrash + LogStartupDiagnostic) instead of carrying its own regex.

    /// <summary>Appends one timestamped record per crash. Best-effort: any IO failure
    /// here is silently swallowed — we are already in a crash path.
    /// <para>EXIT-05: the AppendAllText gets ONE short bounded retry on IOException
    /// (the crash often coincides with an AV / Trend-Micro transient lock on the very
    /// file we are trying to write). Mirrors <c>AtomicJsonWriter.TryRead</c>'s escalating
    /// backoff but smaller — 2 quick retries (20/40 ms) rather than 5 — because a crash
    /// path must not stall teardown. A still-locked file after the retries is swallowed
    /// like any other failure: losing the record is acceptable, hanging the exit is not.</para></summary>
    private static void LogCrash(Exception? ex, string source)
    {
        if (ex == null) return;
        try
        {
            // SEC-01: redact any "\Users\<name>\" segment via the shared PathRedactor
            // before persisting so the OS account name never lands in the shipped
            // crash.log. PathRedactor never throws and is a no-op when there is no
            // path to redact.
            string redacted = SenseEase.Infrastructure.Storage.PathRedactor.Redact(ex.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            string record =
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] =====" + Environment.NewLine
                + redacted + Environment.NewLine + Environment.NewLine;

            // EXIT-05: one short bounded retry on a transient IOException (file lock).
            const int maxAttempts = 3; // initial try + 2 retries
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.AppendAllText(CrashLogPath, record);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    try { Thread.Sleep(20 * attempt); } catch { /* ignore */ }
                }
            }
        }
        catch { /* never crash inside the crash handler */ }
    }
}
