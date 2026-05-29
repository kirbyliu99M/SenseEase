using System;
using System.Collections.Generic;
using System.IO;

namespace SenseEase.App.Configuration;

public enum SensingMode
{
    Demo,   // Phase 1: MockSensingProvider driven by OFI slider
    Cursor, // Phase 2.1: CursorVelocityProvider (Win32 GetCursorPos 30 Hz)
    Wgc,    // Phase 2.2: WgcSensingProvider (Windows.Graphics.Capture pixel-diff 15 Hz)
}

public enum LuxSensingMode { Real, Mock }

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
        if (File.Exists(inBin)) { ModelPathProbes = probes; return inBin; }

        // 2) Walk up to 8 levels looking for a sibling models/ folder.
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
                return candidate;
            }
        }

        ModelPathProbes = probes;
        return inBin; // canonical-install path for the "not found" diagnostic
    }

    // Demo Mode video file. Demo Mode now runs a real vection clip through the
    // WGC pixel-diff sensor (instead of the old OFI slider ramp), so we resolve
    // the same way as ModelPath: try the EXE-bundled samples folder first, then
    // walk up to find the repo's samples/ during dev. Empty string when nothing
    // is found — caller falls back to the legacy OFI ramp.
    // Use the H.264 transcode of the original AV1 file. WPF MediaElement on
    // stock Windows can't decode AV1 reliably even with the MS Store AV1
    // extension installed (the source clip's specific profile flags trip the
    // extension's MF integration); H.264 plays natively on every Windows since
    // Vista. The AV1 master file is retained in samples/ for reference but
    // gitignored alongside the H.264 transcode (samples/*.mp4).
    private const string DemoVideoRelative = "samples/FPV-vid-h264.mp4";

    public static string DemoVideoPath { get; } = ResolveDemoVideoPath();

    public static IReadOnlyList<string> DemoVideoProbes { get; private set; } = Array.Empty<string>();

    private static string ResolveDemoVideoPath()
    {
        var probes = new List<string>();
        string rel = DemoVideoRelative.Replace('/', Path.DirectorySeparatorChar);
        string baseDir = AppDomain.CurrentDomain.BaseDirectory
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string inBin = Path.Combine(baseDir, rel);
        probes.Add(inBin);
        if (File.Exists(inBin)) { DemoVideoProbes = probes; return inBin; }

        var info = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && info is not null; i++)
        {
            info = info.Parent;
            if (info is null) break;
            string candidate = Path.Combine(info.FullName, rel);
            probes.Add(candidate);
            if (File.Exists(candidate)) { DemoVideoProbes = probes; return candidate; }
        }

        DemoVideoProbes = probes;
        return string.Empty;
    }

    // Phase 2.4: per-user personalization adjustment log (JSON Lines, survives restarts).
    public static string AdjustmentLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "adjustments.jsonl");

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

    // Phase 4 — Ambient Light Comfort. Hardware-mutating; defaults OFF (explicit opt-in).
    public static bool AmbientLightComfortEnabled { get; set; } = false;
    public static LuxSensingMode LuxMode { get; set; } = LuxSensingMode.Real;
    public static string DisplayBaselinePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "display-baseline.json");

    // Phase 4 fallback — when the device has no Windows ALS (the typical case on most
    // laptops and all desktops), estimate lux from the webcam's average frame luminance.
    // DEFAULT ON per user direction: insisting on non-existent ALS hardware leaves the
    // entire ambient-comfort pipeline dormant on the majority of target devices, so the
    // pragmatic answer is to use the RGB camera that exists. Privacy/power notes:
    //   • Opening the webcam prompts Windows for camera permission on first use; denial
    //     is caught and degrades silently to "no lux available" (no crash).
    //   • Polling is 10 s (see WebcamLuxProvider) so battery cost is negligible.
    //   • Webcam auto-exposure means the lux value is ORDINAL ("darker / brighter than
    //     baseline") rather than absolute illuminance — LuxComfortMapper's log curve
    //     plus hysteresis absorbs this; the actuator's DDC/CI writes are unaffected.
    //   • Only fires when `AmbientLightComfortEnabled` is also true (which is OFF by
    //     default — it writes the monitor's NVRAM, separate opt-in).
    public static bool UseWebcamLuxFallback { get; set; } = true;

    // Phase 2.1 fix: reject OFI accumulated by user-driven cursor activity (window drags,
    // selections, scrollbar drags). Self-generated motion does not induce vection — the
    // user has proprioceptive awareness — so it should not raise VIMS pressure. Default
    // ON because the previous behavior was a false-positive source.
    public static bool RejectCursorDragOfi { get; set; } = true;

    // Auto Driver / Passenger mode (user feedback "自動 passenger & Driver mode 或可實裝").
    // Master switch for UserActivityModeDetector. When true (default), MainViewModel
    // subscribes to the detector at startup so MainViewModel.Mode automatically tracks
    // OS-level input idleness via Win32 GetLastInputInfo. Manual ToggleModeCommand sets
    // this back to false at runtime, surrendering automation in favour of explicit user
    // control for the remainder of the session.
    public static bool AutoDriverPassengerModeEnabled { get; set; } = true;

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

    // M4 (head-tracking sprint): paths to the Python sidecar's interpreter
    // and script. Resolved with the same "alongside the EXE, then walk up
    // looking for the repo's tools/ folder" probe used for ModelPath /
    // DemoVideoPath so both development and shipping installs work. Only
    // consulted when HeadTrackingEnabled is true; the probe is cached at
    // first access. Default sidecar layout (committed at the M2 tag):
    //   tools/head-tracker-sidecar/.venv/Scripts/python.exe   (Windows)
    //   tools/head-tracker-sidecar/head_tracker.py
    private const string SidecarPythonRelative =
        "tools/head-tracker-sidecar/.venv/Scripts/python.exe";
    private const string SidecarScriptRelative =
        "tools/head-tracker-sidecar/head_tracker.py";

    /// <summary>M4: resolved absolute path to the sidecar's Python
    /// interpreter, or the canonical (alongside-EXE) candidate if not
    /// found. SidecarHeadPoseReader treats "file not found" as a soft
    /// disable (logs + ReadLatest returns null), so a missing venv on a
    /// fresh install never crashes the host.</summary>
    public static string SidecarPythonPath { get; } =
        ResolveSidecarPath(SidecarPythonRelative);

    /// <summary>M4: resolved absolute path to head_tracker.py.</summary>
    public static string SidecarScriptPath { get; } =
        ResolveSidecarPath(SidecarScriptRelative);

    private static string ResolveSidecarPath(string relative)
    {
        string rel = relative.Replace('/', Path.DirectorySeparatorChar);
        string baseDir = AppDomain.CurrentDomain.BaseDirectory
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string inBin = Path.Combine(baseDir, rel);
        if (File.Exists(inBin)) return inBin;

        var info = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && info is not null; i++)
        {
            info = info.Parent;
            if (info is null) break;
            string candidate = Path.Combine(info.FullName, rel);
            if (File.Exists(candidate)) return candidate;
        }
        return inBin; // canonical-install path for the "not found" diagnostic
    }
}
