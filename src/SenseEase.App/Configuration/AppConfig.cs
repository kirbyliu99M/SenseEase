using System;
using System.Collections.Generic;
using System.IO;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.Configuration;

public enum SensingMode
{
    // D-9 (2026-06-11): the Demo (MockSensingProvider) and Cursor (CursorVelocityProvider)
    // modes were retired (FEAT-11/DEAD-03). WGC is the sole sensing source. The enum keeps a
    // single member; SensingMode is not persisted to disk (AppConfig.Mode is a runtime static
    // pinned to Wgc), so no tolerant parse of legacy "Demo"/"Cursor" values is needed.
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
    /// <summary>TEST-ONLY data-root seam (CW-9). When non-null, every
    /// %LOCALAPPDATA%\SenseEase path below is rooted here instead of the real
    /// per-user LocalApplicationData folder, so the test suite can never append
    /// crisis/injection payloads to — or delete/rewrite — the developer's real
    /// SenseEase data. <c>Environment.GetFolderPath</c> ignores env overrides, so
    /// the old "steer the LOCALAPPDATA env var" trick was a silent no-op; this is
    /// the real seam. PRODUCTION: stays null (the resolver falls through to the OS
    /// folder), so runtime behaviour is byte-identical. Set by the test assembly's
    /// module initializer (see TestDataRootFixture) before any path is read.
    /// <para>The path properties below are computed at ACCESS time (expression-
    /// bodied, not <c>get; }</c>-cached) precisely so this override takes effect
    /// even though static field init order would otherwise read a path before a
    /// test could set it.</para></summary>
    public static string? DataRootOverride { get; set; }

    /// <summary>Single resolver every %LOCALAPPDATA%\SenseEase path routes
    /// through. Returns the test override when set, else the real per-user
    /// LocalApplicationData folder. Combined with "SenseEase" by each caller, so
    /// the override is a bare folder root (not the SenseEase subfolder).
    /// <para>Internal (not private) so the sibling <see cref="UserPreferences"/>
    /// FilePath routes through the SAME root and prefs tests isolate too.</para></summary>
    internal static string DataRoot =>
        DataRootOverride
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // D-9 (2026-06-11): WGC is the sole sensing source. The Cursor (CursorVelocityProvider)
    // and Demo (MockSensingProvider) modes were retired (FEAT-11/DEAD-03), so SensingMode now
    // has the single Wgc member and this stays pinned to it. Kept as a settable static for
    // symmetry with the other runtime flags; nothing flips it at runtime any longer.
    public static SensingMode Mode { get; set; } = SensingMode.Wgc;

    // Phase 5: local Gemma 4 E4B (instruction-tuned, Q4_K_M GGUF).
    // The chatbot is optional — if no GGUF is found, the app falls back to the
    // rule-based FeedbackLexicon path. We probe two locations so both development
    // (file in the repo's models/ folder) and shipping (file alongside the EXE)
    // work without manual moves:
    //   1) <bin>/models/...  — what `dotnet publish` produces if the folder is
    //                          copied to output. Preferred so the EXE is portable.
    //   2) <repo>/models/... — convenient during development; we walk up from the
    //                          bin folder to find a sibling models/ directory.
    // ── Chat-model registry (2026-06-19) ──────────────────────────────────────
    // Qwen3-4B-Instruct-2507 is the SOLE chat model. The Mistral fallback was removed once Qwen proved
    // reliable — there is no second bundled model. The C# layer is model-agnostic (OpenAI-compat +
    // server-side --jinja); a profile only carries the path + stop tokens + any per-model server flags,
    // so swapping or adding a model later is one entry here plus the pref/env key. If the GGUF is absent
    // or fails to load, chat drops to the deterministic lexicon path (LocalLlmChatbotService leaves
    // IsModelLoaded=false) — no in-process model fallback.
    // NpuModelTag (ultrabook #5): the FastFlowLM catalog tag for the SAME model on the AMD Ryzen-AI NPU
    // (e.g. "qwen3-it:4b" = Qwen3-4B-Instruct-2507 in Q4NX). Null when no NPU equivalent is paired. The GGUF
    // path drives the universal Vulkan backend; NpuModelTag drives the NPU backend — both describe one model.
    public sealed record ChatModel(string Key, string ModelPath, string[] StopTokens, string[] ExtraServerArgs, int? NGpuLayers, string? NpuModelTag = null);
    private sealed record ChatModelSpec(string Key, string Relative, string[] StopTokens, string[] ExtraServerArgs, int? NGpuLayers, string? NpuModelTag = null);

    /// <summary>The primary (and currently only) chat model — Qwen3-4B-Instruct-2507 (non-thinking). The
    /// default selection (UserPreferences.ChatModelKey) and the fallback for an unknown/garbage pref key.</summary>
    public const string PrimaryModelKey = "qwen3-4b";
    private static readonly ChatModelSpec[] ModelCatalog =
    {
        // Qwen3-4B-Instruct-2507 is the DEDICATED non-thinking variant → no enable_thinking flags needed.
        new(PrimaryModelKey, "models/Qwen3-4B-Instruct-2507-GGUF/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            new[] { "<|im_end|>", "User:" }, Array.Empty<string>(), null, NpuModelTag: "qwen3-it:4b"),
    };

    private static readonly Lazy<ChatModel> _activeModel = new(ResolveActiveModel);
    /// <summary>The model to load: env override → persisted pref → PrimaryModelKey (unknown key → primary).</summary>
    public static ChatModel ActiveModel => _activeModel.Value;

    /// <summary>Back-compat: the ACTIVE model's resolved path (existing diagnostics + call sites).</summary>
    public static string ModelPath => ActiveModel.ModelPath;

    private static ChatModelSpec? FindSpec(string key)
    {
        foreach (var s in ModelCatalog) if (s.Key == key) return s;
        return null;
    }

    private static ChatModel BuildModel(ChatModelSpec s)
        => new(s.Key, ResolveModelPath(s.Relative), s.StopTokens, s.ExtraServerArgs, s.NGpuLayers, s.NpuModelTag);

    private static ChatModel ResolveActiveModel()
    {
        // dev env override → persisted pref → primary. Unknown/garbage key → primary.
        string key = Environment.GetEnvironmentVariable("SENSEEASE_CHAT_MODEL") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            try { key = UserPreferences.Current.ChatModelKey ?? PrimaryModelKey; }
            catch { key = PrimaryModelKey; }
        }
        var spec = FindSpec(key.Trim()) ?? FindSpec(PrimaryModelKey)!;
        return BuildModel(spec);
    }

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

    private static string ResolveModelPath(string relativePath)
    {
        var probes = new List<string>();
        string rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
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
    // CW-9: computed at access (routes through DataRoot) so the test seam isolates it.
    public static string AdjustmentLogPath => Path.Combine(
        DataRoot, "SenseEase", "adjustments.jsonl");

    // H17: directory for the durable, daily-rolled JSONL audit logs (event log +
    // chatbot-action log) — the forensic trail that survives restarts.
    public static string AuditLogDir => Path.Combine(
        DataRoot, "SenseEase", "logs");

    // Habituation profile snapshot (Phase 4). Independent of habituation.json
    // (which continues to store offset only). See docs/HABITUATION-PROFILE.md
    // §5 for the schema + write-trigger semantics. Writes via tmp + File.Replace
    // (atomic across Process.Kill mid-write).
    public static string HabituationProfilePath => Path.Combine(
        DataRoot, "SenseEase", "habituation_profile.json");

    // B6 Stage 4b: the bidirectional elasticity classifier's durable UP-qualify streak (consecutive-eval
    // count + LastQualifyUtc spacing). Written only on a consent-ON tuning pass; cleared on a Reset.
    public static string ProfileEvalHistoryPath => Path.Combine(
        DataRoot, "SenseEase", "profile_eval_history.json");

    // First-run flag. Touched once after the user completes (or skips) the
    // setup-demo calibration. Used to drive the welcome banner on the dashboard:
    // banner is shown only when the flag file does NOT exist. Flag file is empty
    // — its presence is the entire signal. Stored alongside adjustments.jsonl
    // so a user clearing %LocalAppData%\SenseEase\ also resets first-run state.
    public static string FirstRunFlagPath => Path.Combine(
        DataRoot, "SenseEase", "first-run-complete.flag");

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
    public static string HabituationStatePath => Path.Combine(
        DataRoot, "SenseEase", "habituation.json");

    // Phase 4: chat transcript (JSON Lines, best-effort). Survives restarts so the
    // returning user sees prior conversation context.
    public static string ChatHistoryPath => Path.Combine(
        DataRoot, "SenseEase", "chat_history.jsonl");

    // Phase 5b: calibration-mode LLM transcript (JSON Lines, best-effort). Separate
    // from chat_history.jsonl so VIMS-monitoring context never bleeds into the
    // calibration dialog's longitudinal-comparison reasoning.
    public static string CalibrationHistoryPath => Path.Combine(
        DataRoot, "SenseEase", "calibration_history.jsonl");

    // UM1 (User Model sprint): daily passive behaviour aggregate — append-only
    // JSONL, one numeric-only row per day (NO raw utterances / OFI traces / window
    // titles, per the sprint's no-PII constraint). Same %LOCALAPPDATA%\SenseEase\
    // home as the other user data so H25 uninstall + H24 clear cover it.
    public static string BehaviorLogPath => Path.Combine(
        DataRoot, "SenseEase", "behavior_log.jsonl");

    // UM3 (User Model sprint): incremental analysis index + bucket manifest.
    // Atomic write (tmp + File.Replace) like habituation_profile.json.
    public static string UserModelIndexPath => Path.Combine(
        DataRoot, "SenseEase", "user_model_index.json");

    // Beta-1 Phase-1 (the shared EventLog foundation): the structured symptom/comfort event log
    // — the explicit FMS self-report (D-18/19) + the daily NL→FMS estimate (D-21), one
    // SymptomEvent per line. Same %LOCALAPPDATA%\SenseEase home so H24 clear + H25 uninstall
    // cover it. Exportable=false on every row (export is the de-identified aggregate path, D-17).
    public static string SymptomLogPath => Path.Combine(
        DataRoot, "SenseEase", "symptom_log.jsonl");

    // FMS owner-migration (chatbot-tuning sprint 2026-06-19): a DEDICATED, clean one-row-per-response log
    // of the in-chat FMS 0–20 quick-rate check-in, for DEV MONITORING — parallel to the NPS
    // survey_log.jsonl, in the SAME %LOCALAPPDATA%\SenseEase home (H24 clear + H25 uninstall cover it).
    // Distinct from symptom_log.jsonl (which mixes spike reports + NL estimates + other kinds).
    public static string FmsLogPath => Path.Combine(
        DataRoot, "SenseEase", "fms_log.jsonl");

    // Learning-loop Part D / Idea #2 (2026-06-21): one row per app-launch recording the FIRST discomfort
    // signal of the session (consent-accept | FMS | chatbot ReportDiscomfort) + its elapsed offset — the
    // "typical time to first discomfort" capture (see SessionOnsetLog). DELIBERATELY separate from the
    // collision-hot behavior_log.jsonl the health-report build reads. Same %LOCALAPPDATA%\SenseEase home so
    // H24 clear + H25 uninstall cover it; numeric/categorical only (no PII).
    public static string SessionOnsetLogPath => Path.Combine(
        DataRoot, "SenseEase", "session_onset_log.jsonl");

    // H24: the chatbot model trace (the ClearOnDiskTrace target). Mirrors the path
    // LocalLlmChatbotService writes to (Infrastructure can't see this App config, so
    // both sides compute %LOCALAPPDATA%\SenseEase\model_trace.jsonl independently).
    // Used by the Settings "clear all my data" button to delete it directly.
    public static string ModelTracePath => Path.Combine(
        DataRoot, "SenseEase", "model_trace.jsonl");

    // Display Comfort AppConfig fields (AmbientLightComfortEnabled, LuxMode,
    // DisplayBaselinePath, UseWebcamLuxFallback) removed at v0.11 (UI
    // Consolidation Sprint Phase 4) — feature decommissioned. The
    // display-baseline.json file at the historical %LOCALAPPDATA%\SenseEase
    // path is deleted once on app startup (App.OnStartup migration). See
    // CHANGELOG.md v0.11 "Removed".

    // D-9 (2026-06-11): RejectCursorDragOfi removed with the CursorVelocityProvider
    // it configured (FEAT-11/DEAD-03). WGC OFI rejection lives in WindowsActivityFilter.

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
    /// decision). The new-user-stranding risk the original M1-gating guarded against is
    /// covered because the selector is live (it can adapt the level) and M1 is a validation
    /// follow-up, not a prerequisite.
    ///
    /// <para><b>FEAT-07 / D-8 (2026-06-11): the flag no longer gates the seed.</b> The fresh-user
    /// TYPICAL / L5「中」 seeding in <c>MockInferenceEngine.ApplyAutoProfilingStartDefaults</c>
    /// now runs REGARDLESS of this flag (the old <c>if (!_autoProfilingEnabled) return;</c> guard
    /// was removed). With D-13 having DELETED the explicit-calibration plumbing, a flag-off path
    /// that "restored RESILIENT/L1 + the calibration flow" could only ever strand a fresh user
    /// with no onboarding — so flag-off now means: a fresh user STILL seeds at TYPICAL / L5,
    /// and ONLY the App-side signals react to the flag (the vestibular first-use prompt, console
    /// visibility, and selector-adjacent UI). The seeder never touches the earned habituation
    /// offset (Q4.5-safe).</para>
    ///
    /// <para><b>D-13 (2026-06-11): explicit calibration is DELETED, not dormant.</b>
    /// <c>CalibrationProcessor.Process</c> and <c>MockInferenceEngine.ApplyCalibrationResult</c>
    /// were removed (the 5-card → CalibrationResult → engine-state pipeline no longer exists);
    /// vestibular diagnosis is captured by the one-time first-use field + the permanent Settings
    /// toggle (<c>SetVestibularProtection</c>) instead of calibration Q5. Setting this flag to
    /// <c>false</c> does NOT bring the explicit-calibration flow back — that requires
    /// <c>git revert 3131387</c> to re-add the deleted plumbing.</para>
    ///
    /// <para>Same settable-static pattern as the other runtime feature flags here.</para></summary>
    // ACTIVATED 2026-06-07 (Kirby: both flags on). Explicit calibration is CANCELLED and, since
    // D-13, its application plumbing is DELETED (not just dormant): a fresh user seeds at
    // L5「中」/TYPICAL regardless of this flag (D-8), and vestibular diagnosis is captured by the
    // one-time first-use field + the permanent Settings toggle (re-rooted; the vestibular
    // consumers still fire). Restoring the explicit-calibration flow requires git revert 3131387.
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

    /// <summary>Beta-1 D-16 / theme-2: opt-in per-app OFI attribution. Default FALSE (privacy:
    /// recording which apps a user runs is sensitive, health-adjacent telemetry — D-16 requires
    /// explicit consent). When true, WindowsActivityFilter resolves the foreground app to a
    /// coarse category + name key (ON-DEVICE only; never off-device — D-16) that the symptom
    /// recorder stamps onto data-points for the Theme-5 join. Mirrors the head-tracking privacy
    /// contract; flipping requires an explicit consent toggle (settings UI follow-on).</summary>
    public static bool PerAppAttributionEnabled { get; set; } = false;

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
    ///         stable as the dark periphery shifts more dramatically.</item>
    /// </list></summary>
    public static HeadTrackingFollowModeKind HeadTrackingFollowMode { get; set; }
        = HeadTrackingFollowModeKind.Balanced;

    // D-10 (2026-06-11): HeadTrackingEscapeHatchEnabled removed. The double-tap-Ctrl
    // escape hatch (temporarily dimming the FOV mask to 30 % for 3 s) was retired
    // entirely — the feature is gone, not deferred (FEAT-06 cancelled). See
    // docs/design/HARD_CONSTRAINTS.md A3 for the conservative-direction rationale
    // (the mask is never weakened; the vestibular force-disable is now vacuous).

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
