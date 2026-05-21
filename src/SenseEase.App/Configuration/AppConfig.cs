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

    // Phase 2.4: local Mistral 3B model — drop the .gguf here.
    public static string ModelPath { get; } = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "models", "mistral-3b", "model.gguf");

    // Phase 2.4: per-user personalization adjustment log (JSON Lines, survives restarts).
    public static string AdjustmentLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "adjustments.jsonl");

    // Phase 3: habituation progress — persisted so re-habituation survives restarts.
    public static string HabituationStatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "habituation.json");

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
