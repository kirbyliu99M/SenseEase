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

/// <summary>
/// Runtime feature flags. Change Mode to switch sensing providers without restart.
/// </summary>
public static class AppConfig
{
    public static SensingMode Mode { get; set; } = SensingMode.Cursor;

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

    // Phase 2.4: per-user personalization adjustment log (JSON Lines, survives restarts).
    public static string AdjustmentLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "adjustments.jsonl");

    // Phase 3: habituation progress — persisted so re-habituation survives restarts.
    public static string HabituationStatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "habituation.json");

    // Phase 4: chat transcript (JSON Lines, best-effort). Survives restarts so the
    // returning user sees prior conversation context.
    public static string ChatHistoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "chat_history.jsonl");

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
}
