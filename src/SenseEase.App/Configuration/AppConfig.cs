using System;
using System.Collections.Generic;
using System.IO;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.Configuration;

public enum SensingMode
{
    Demo,   // Phase 1: MockSensingProvider driven by OFI slider
    Cursor, // Phase 2.1: CursorVelocityProvider (Win32 GetCursorPos 30 Hz)
    Wgc,    // Phase 2.2: WgcSensingProvider (Windows.Graphics.Capture pixel-diff 15 Hz)
}

// LuxSensingMode enum removed at v0.11 (UI Consolidation Sprint Phase 4) —
// Display Comfort feature decommissioned. See CHANGELOG.md v0.11 "Removed".

/// <summary>M5.1-B follow-mode choice. See
/// <see cref="AppConfig.HeadTrackingFollowMode"/> for semantics.</summary>
public enum HeadTrackingFollowModeKind
{
    /// <summary>Mask center clamped to [0.10, 0.90] — periphery always
    /// partially dimmed. Default; aligned with the sprint's clinical
    /// VIMS-mitigation purpose.</summary>
    Balanced,

    /// <summary>Mask center in [0, 1] — follows gaze all the way to
    /// screen corners. Opt-in; may feel less stable.</summary>
    FullFollow,
}

/// <summary>
/// Runtime feature flags. Change Mode to switch sensing providers without restart.
/// </summary>
public static class AppConfig
{
    // Path Z UX iter 3 (2026-05-27): default flipped from Cursor → Wgc. The
    // Cycle Sensing Mode button is now hidden (Cursor mode unused, Demo only
    // entered via Calibration), so the startup provider must be the real-
    // measurement one. Without this flip, the app would silently boot on
    // CursorVelocityProvider with no way for the user to switch — a ghost
    // pipeline running with no UI surface to control it.
    public static SensingMode Mode { get; set; } = SensingMode.Wgc;

    // Backward-compat: true only in Demo mode (used by idleTimer and slider Push guard).
    public static bool UseMockSensing => Mode == SensingMode.Demo;

    // Phase 5: local Gemma 4 E4B (instruction-tuned, Q4_K_M GGUF).
    // The chatbot is optional — if no GGUF is found, the app falls back to the
    // rule-based FeedbackLexicon path. We probe two locations so both development
    // (file in the repo's models/ folder) and shipping (file alongside the EXE)
    // work without manual moves:
    //   1) <bin>/models/...  — what `dotnet publish` produces if the folder is
    //                          copied to output. Preferred so the EXE is portable.
    //   2) <repo>/models/... — convenient during development; we walk up from the
    //                          bin folder to find a sibling models/ directory.
    // Phase 5b: switched back to Ministral 3B after Gemma 4's GGUF tokenizer
    // hit an ABI gap with LLamaSharp 0.27 — the b9294 native binaries worked for
    // llama-cli directly but LLamaSharp's tokenize-without-special-parsing path
    // produced only <unused24>/<unused49> filler tokens. Ministral 3B uses the
    // Mistral chat template, which LLamaSharp 0.27 was built/tested against,
    // and it's the same model the prior PC ran successfully.
    private const string ModelFileRelative = "models/Ministral-3-3B-Instruct-2512-GGUF/Ministral-3-3B-Instruct-2512-Q4_K_M.gguf";

    public static string ModelPath { get; } = ResolveModelPath();

    /// <summary>Returns the list of paths probed by ResolveModelPath, in order.
    /// Surfaced to the UI when the model isn't found so the user can see exactly
    /// where the app looked.</summary>
    public static IReadOnlyList<string> ModelPathProbes { get; private set; } = Array.Empty<string>();

    /// <summary>H50: reparse-point guard for a resolved model/data/video path.
    /// Validates the path against its OWN containing directory (the install or
    /// dev folder it was resolved from — never hardcoded), so a normal file is
    /// returned byte-for-byte unchanged; only a symlinked / junctioned file (or
    /// one whose existing parent is a reparse point) is rejected via
    /// <see cref="PathValidator.EnsureSafe"/> (throws
    /// <see cref="System.Security.SecurityException"/>). Empty paths are passed
    /// through untouched (the "not found" diagnostic uses an empty string).</summary>
    private static string GuardResolvedPath(string resolved)
    {
        if (string.IsNullOrEmpty(resolved))
            return resolved;
        string? dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
            PathValidator.EnsureSafe(resolved, dir);
        return resolved;
    }

    private static string ResolveModelPath()
    {
        var probes = new List<string>();
        string rel = ModelFileRelative.Replace('/', Path.DirectorySeparatorChar);
        // Strip any trailing slash from BaseDirectory so DirectoryInfo.Parent walks
        // up reliably (with a trailing slash some .NET versions treat it as already
        // pointing at the parent — the walk silently no-ops).
        string baseDir = AppDomain.CurrentDomain.BaseDirectory
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 1) Alongside the EXE (canonical install location after publish).
        string inBin = Path.Combine(baseDir, rel);
        probes.Add(inBin);
        if (File.Exists(inBin)) { ModelPathProbes = probes; return GuardResolvedPath(inBin); }

#if DEBUG
        // 2) Walk up to 8 levels looking for a sibling models/ folder.
        //    H40: dev-only convenience. The walk-up can resolve to a path
        //    OUTSIDE the install directory (a sibling repo checkout, a parent
        //    folder), which is fine during development but is a path-confusion
        //    hazard in a shipped build — so it is gated behind #if DEBUG.
        var info = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && info is not null; i++)
        {
            info = info.Parent;
            if (info is null) break;
            string candidate = Path.Combine(info.FullName, rel);
            probes.Add(candidate);
            if (File.Exists(candidate))
            {
                ModelPathProbes = probes;
                return GuardResolvedPath(candidate);
            }
        }
#endif

        ModelPathProbes = probes;
        // "not found" diagnostic path: the file does not exist, so the guard
        // validates the (normal) install directory and returns it unchanged.
        return GuardResolvedPath(inBin);
    }

    // Demo/calibration video REMOVED (2026-06-08): the vection-clip demo and its
    // WPF MediaElement player were excised (no codec dependency, no bundled video).
    // DemoVideoRelative / DemoVideoPath / DemoVideoProbes / ResolveDemoVideoPath are gone.

    // Phase 2.4: per-user personalization adjustment log (JSON Lines, survives restarts).
    public static string AdjustmentLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "adjustments.jsonl");

    // H17: directory for the durable, daily-rolled JSONL audit logs (event log +
    // chatbot-action log) — the forensic trail that survives restarts.
    public static string AuditLogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "logs");

    // Habituation profile snapshot (Phase 4). Independent of habituation.json
    // (which continues to store offset only). See docs/HABITUATION-PROFILE.md
    // §5 for the schema + write-trigger semantics. Writes via tmp + File.Replace
    // (atomic across Process.Kill mid-write).
    public static string HabituationProfilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "habituation_profile.json");

    // First-run flag. Touched once after the user completes (or skips) the
    // setup-demo calibration. Used to drive the welcome banner on the dashboard:
    // banner is shown only when the flag file does NOT exist. Flag file is empty
    // — its presence is the entire signal. Stored alongside adjustments.jsonl
    // so a user clearing %LocalAppData%\SenseEase\ also resets first-run state.
    public static string FirstRunFlagPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "first-run-complete.flag");

    public static bool IsFirstRun => !File.Exists(FirstRunFlagPath);

    public static void MarkFirstRunComplete()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FirstRunFlagPath)!);
            File.WriteAllText(FirstRunFlagPath, DateTime.UtcNow.ToString("o"));
        }
        catch { /* best-effort — worst case the banner shows again next launch */ }
    }

    // Phase 3: habituation progress — persisted so re-habituation survives restarts.
    public static string HabituationStatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "habituation.json");

    // Phase 4: chat transcript (JSON Lines, best-effort). Survives restarts so the
    // returning user sees prior conversation context.
    public static string ChatHistoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "chat_history.jsonl");

    // Phase 5b: calibration-mode LLM transcript (JSON Lines, best-effort). Separate
    // from chat_history.jsonl so VIMS-monitoring context never bleeds into the
    // calibration dialog's longitudinal-comparison reasoning.
    public static string CalibrationHistoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "calibration_history.jsonl");

    // UM1 (User Model sprint): daily passive behaviour aggregate — append-only
    // JSONL, one numeric-only row per day (NO raw utterances / OFI traces / window
    // titles, per the sprint's no-PII constraint). Same %LOCALAPPDATA%\SenseEase\
    // home as the other user data so H25 uninstall + H24 clear cover it.
    public static string BehaviorLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "behavior_log.jsonl");

    // UM3 (User Model sprint): incremental analysis index + bucket manifest.
    // Atomic write (tmp + File.Replace) like habituation_profile.json.
    public static string UserModelIndexPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "user_model_index.json");

    // H24: the chatbot model trace (the ClearOnDiskTrace target). Mirrors the path
    // MistralChatbotService writes to (Infrastructure can't see this App config, so
    // both sides compute %LOCALAPPDATA%\SenseEase\model_trace.jsonl independently).
    // Used by the Settings "clear all my data" button to delete it directly.
    public static string ModelTracePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "model_trace.jsonl");

    // Display Comfort AppConfig fields (AmbientLightComfortEnabled, LuxMode,
    // DisplayBaselinePath, UseWebcamLuxFallback) removed at v0.11 (UI
    // Consolidation Sprint Phase 4) — feature decommissioned. The
    // display-baseline.json file at the historical %LOCALAPPDATA%\SenseEase
    // path is deleted once on app startup (App.OnStartup migration). See
    // CHANGELOG.md v0.11 "Removed".

    // Phase 2.1 fix: reject OFI accumulated by user-driven cursor activity (window drags,
    // selections, scrollbar drags). Self-generated motion does not induce vection — the
    // user has proprioceptive awareness — so it should not raise VIMS pressure. Default
    // ON because the previous behavior was a false-positive source.
    public static bool RejectCursorDragOfi { get; set; } = true;

    // Pop-up revision (Kirby 2026-06-06): the persistent ambient BAR is disabled — the
    // tray icon is the sole access point (it already carries the 5級 ambient colour +
    // paused/faulted status) and the card is summoned from the tray as a bottom hover
    // flyout. Default OFF; flip to re-enable the bar (the bar code is retained, dormant).
    public static bool PersistentBarEnabled { get; set; } = false;

    // Auto Driver / Passenger mode (user feedback "自動 passenger & Driver mode 或可實裝").
    // Master switch for UserActivityModeDetector. When true (default), MainViewModel
    // subscribes to the detector at startup so MainViewModel.Mode automatically tracks
    // OS-level input idleness via Win32 GetLastInputInfo. Phase 5 (v0.11, 2026-05-31)
    // removed the manual Dashboard toggle; no code path flips this flag at runtime any
    // longer — kept as a public static setter so config persistence, tests, or a future
    // Settings UI can opt out of automation if needed.
    public static bool AutoDriverPassengerModeEnabled { get; set; } = true;

    /// <summary>Alpha-2 core GROUP A: master gate for the entire Adaptive Fit
    /// selector (the 50-set grid + ResolveLevel/SetLevel + the stats/selector/
    /// state-machine pipeline). ACTIVATED 2026-06-07 (default <c>true</c>, see the
    /// activation note below): the selector acts on the SAME provisional OFI scale the
    /// live overlay already uses; M1 capture VALIDATES/refines that factor as a follow-up,
    /// it is NOT a prerequisite. Set to <c>false</c> to fully revert to today's static path.
    ///
    /// <para><b>Flag-off = today's behaviour byte-identical.</b> When set to
    /// <c>false</c> (revert) NOTHING on the hot path reads the new grid:
    /// <c>MockInferenceEngine.Update</c> runs the existing
    /// <c>VimsLevels</c>/<c>ComposeFovRadius</c> path unchanged and never
    /// consults <c>SetGrid</c>/<c>ResolveLevel</c>. The new path is reachable
    /// ONLY when this flag is <c>true</c>; even then, at the pinned median
    /// <c>GridCoord.Set0</c> the resolved mask alpha / FOV floor equal today's
    /// values (set0 ≡ today), so flipping the flag at set0 is a no-op.</para>
    ///
    /// <para>Same settable-static pattern as the other runtime feature flags
    /// here (AppConfig is a static class — no init-only properties); flipping
    /// requires explicit opt-in in code or a future settings UI.</para></summary>
    // ACTIVATED 2026-06-07 (Kirby: "兩個flag同時來" — break the M1 gate, both flags on).
    // The 50-set/12-level Adaptive-Fit selector now actively decides set-moves. It acts on
    // the SAME provisional OFI scale (WgcMadNormFactor) the live overlay already uses today;
    // M1 capture (baseline/M1-CALIBRATION-README.md) VALIDATES/refines that factor — it is a
    // follow-up, not a prerequisite. Set back to false to fully revert to today's static path.
    public static bool AdaptiveFitSelectorEnabled { get; set; } = true;

    /// <summary>Alpha-2 addendum (cancel explicit calibration → auto-profiling):
    /// master gate for the zero-setup start. ACTIVATED 2026-06-07 (default <c>true</c>,
    /// coupled with <see cref="AdaptiveFitSelectorEnabled"/> per Kirby's "both flags on"
    /// decision; see the activation note below). The new-user-stranding risk the original
    /// M1-gating guarded against is covered because the selector is live (it can adapt the
    /// level) and M1 is a validation follow-up, not a prerequisite. Set to <c>false</c> to
    /// restore the explicit-calibration flow (the calibration code is suppressed-not-removed).
    ///
    /// <para><b>Flag-off = today's behaviour byte-identical.</b> When set to <c>false</c>
    /// (revert): the explicit 5-card / demo-reaction / LLM-dialog calibration
    /// flow runs exactly as today, the new-user defaults stay RESILIENT/L1, the
    /// console keeps its Calibration &amp; Demo + Reset entries, and the one-time
    /// vestibular field never appears. When <c>true</c>: the calibration entry
    /// points are suppressed (the calibration CODE stays intact, just dormant),
    /// a brand-new user starts at TYPICAL / L5「中」 and the level auto-converges
    /// via the selector, and vestibular diagnosis is captured by a one-time
    /// first-use field + a permanent Settings toggle instead of calibration Q5.</para>
    ///
    /// <para>Same settable-static pattern as the other runtime feature flags here.</para></summary>
    // ACTIVATED 2026-06-07 (Kirby: both flags on). Explicit calibration is now cancelled:
    // the console calibration entry + post-demo Q&A are suppressed, a fresh user starts at
    // L5「中」/TYPICAL, and vestibular diagnosis is captured by the one-time first-use field +
    // the permanent Settings toggle (re-rooted; the 9 vestibular consumers still fire). Set
    // back to false to restore the explicit-calibration flow (code is suppressed-not-removed).
    public static bool AutoProfilingEnabled { get; set; } = true;

    /// <summary>M3 (head-tracking sprint): opt-in flag for head-pose-driven
    /// FOV mask centering. Defaults to false (privacy + stability). When
    /// false, all head-tracking code paths are dead — overlay behaves exactly
    /// like v0.9 baseline (cx=0.5, cy=0.5 hardcoded center).
    ///
    /// <para>AppConfig is a static class (no init-only properties available),
    /// so this is exposed as a settable property for symmetry with the other
    /// runtime feature flags above. Default = false; flipping requires
    /// explicit opt-in either at startup or via a future settings UI.</para></summary>
    public static bool HeadTrackingEnabled { get; set; } = false;

    /// <summary>M5.1-B (head-tracking sprint, 2026-05-30): user-facing
    /// "how much should the mask follow head gaze" choice.
    ///
    /// <list type="bullet">
    ///   <item><c>Balanced</c> — DEFAULT. cx/cy clamped to [0.10, 0.90].
    ///         Mask center stays in the central 80 % of the screen so the
    ///         periphery is always partially dimmed regardless of gaze
    ///         direction. Aligned with the sprint's clinical purpose
    ///         (peripheral motion is the VIMS trigger; the mask is there
    ///         to dim it). Most users want this.</item>
    ///   <item><c>FullFollow</c> — opt-in. cx/cy in [0, 1]. The mask can
    ///         follow gaze all the way to a screen corner. May feel less
    ///         stable as the dark periphery shifts more dramatically.
    ///         Use the escape-hatch hotkey if you only occasionally need
    ///         to peek at a corner.</item>
    /// </list></summary>
    public static HeadTrackingFollowModeKind HeadTrackingFollowMode { get; set; }
        = HeadTrackingFollowModeKind.Balanced;

    /// <summary>M5.1-B: when true (default), double-tapping Ctrl
    /// temporarily dims the FOV mask to 30 % opacity for 3 seconds with
    /// a 250 ms fade-down + 500 ms fade-up. Lets a Balanced-mode user
    /// see a screen corner without permanently switching to FullFollow.
    /// Orthogonal to the follow-mode choice — works in any mode.</summary>
    public static bool HeadTrackingEscapeHatchEnabled { get; set; } = true;

    /// <summary>M8 head-tracking sprint (2026-05-30): debug-only telemetry
    /// flag gating the per-5-seconds <c>[HeadPoseRender]</c> log line in
    /// <c>MainWindow.OnCompositionRendering</c>. The two underlying counters
    /// (WPF render-frame count, sidecar received-sample count) were added
    /// during M5.1-C PRECONDITION measurement to answer the advisor's
    /// "is the dispatcher contention or the sidecar?" question — they
    /// proved their value at G6 perf decision but are dead weight in
    /// production (5-second log spam in wgc_diag.txt, no user value).
    ///
    /// <para>Default <c>false</c> — production builds emit no
    /// <c>[HeadPoseRender]</c> lines. The counters still tick
    /// (<c>Interlocked.Increment</c> is negligible) but the log emission
    /// + <see cref="SenseEase.Infrastructure.Real.SidecarHeadPoseReader.ConsumeReceivedSampleCount"/>
    /// reset are skipped.</para>
    ///
    /// <para>Flip to <c>true</c> in code (same pattern as
    /// <see cref="HeadTrackingEnabled"/>) when debugging a head-tracking
    /// perf regression. After flipping + republishing, the log lines
    /// reappear every 5 seconds as before.</para></summary>
    public static bool HeadTrackingPerfLogEnabled { get; set; } = false;

    // Pre-Alpha v0.11.1 (packaging): PyInstaller --onedir bundle replaces
    // the venv + script pair. The frozen exe lives at
    //   tools/head-tracker-sidecar/dist/head_tracker/head_tracker.exe
    // alongside _internal/ (Python runtime + mediapipe + cv2) and the
    // face_landmarker.task model. Same probe pattern: try the canonical
    // EXE-adjacent path first, then walk up looking for the repo's tools/
    // folder so both dev (run from repo) and shipping (installed) work.
    // Soft-disable contract from M4 is preserved -- SidecarHeadPoseReader
    // treats "file not found" as a no-op (ReadLatest returns null).
    private const string SidecarExeRelative =
        "tools/head-tracker-sidecar/dist/head_tracker/head_tracker.exe";

    /// <summary>Pre-Alpha v0.11.1: resolved absolute path to the frozen
    /// head_tracker.exe, or the canonical (alongside-EXE) candidate if not
    /// found. Replaces SidecarPythonPath + SidecarScriptPath from M4.</summary>
    public static string SidecarExePath { get; } =
        ResolveSidecarPath(SidecarExeRelative);

    private static string ResolveSidecarPath(string relative)
    {
        string rel = relative.Replace('/', Path.DirectorySeparatorChar);
        string baseDir = AppDomain.CurrentDomain.BaseDirectory
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string inBin = Path.Combine(baseDir, rel);
        if (File.Exists(inBin)) return inBin;

#if DEBUG
        // H40: dev-only walk-up (see ResolveModelPath). Gated behind #if DEBUG
        // so a shipped build never launches a sidecar EXE resolved from a path
        // outside the install directory.
        var info = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && info is not null; i++)
        {
            info = info.Parent;
            if (info is null) break;
            string candidate = Path.Combine(info.FullName, rel);
            if (File.Exists(candidate)) return candidate;
        }
#endif

        return inBin; // canonical-install path for the "not found" diagnostic
    }
}
