namespace SenseEase.Core.Models;

/// <summary>
/// Shared state model for the SenseEase core loop (Sensing → Inference → Rendering → Feedback).
/// Matches the spec in the design draft §9. All agents must conform to this contract.
/// </summary>
public class SenseState
{
    /// <summary>Post-attenuation effective OFI used internally by the engine and
    /// overlay (Driver-mode 0.25× + mask-active 0.15–0.45× attenuation applied
    /// inside <c>MockInferenceEngine.Update</c>). This is the value fed into the
    /// leaky integrator and into <c>OverlayWindow</c>'s visual flowTerm. For
    /// user-facing displays (Event Log, history chart, chatbot context) use
    /// <see cref="RawOpticalFlowIndex"/> instead — it carries the raw sensor
    /// reading without attenuation and matches what users see in the dashboard
    /// live OFI indicator (which already binds to the sensor sample directly).</summary>
    public double OpticalFlowIndex { get; set; }

    /// <summary>Raw sensor OFI as received by <c>MockInferenceEngine.Update</c>
    /// before any Driver / mask attenuation. Surface this to all user-facing
    /// OFI displays so the value matches the dashboard's live OFI card (which
    /// binds to <c>MainViewModel.OpticalFlowIndex</c> = the raw sensor sample).
    /// Default 0.0 — populated on every engine tick.</summary>
    public double RawOpticalFlowIndex { get; set; }

    /// <summary>Accumulated visual-stimulus pressure from the Leaky Integrator.</summary>
    public double Pressure { get; set; }

    /// <summary>P1 (personalization-visible): user-facing stress as a percentage of
    /// THIS user's effective trigger (Pressure / EffectiveTrigger × 100, clamped
    /// 0..100). Because EffectiveTrigger is personalized — a SENSITIVE user has a
    /// lower trigger than a RESILIENT one — two users at the SAME raw Pressure see
    /// DIFFERENT DisplayStressPercent, so personalization is finally visible in the
    /// hero metric WITHOUT changing the engine tier. Display-only; the engine never
    /// reads it as input. Default 0; populated on every engine tick.</summary>
    public int DisplayStressPercent { get; set; }

    /// <summary>Inner clear-zone radius of the Chang-Hain FOV mask, in percent.</summary>
    public int FovRadiusPercent { get; set; }

    /// <summary>True once pressure has crossed the trigger threshold.</summary>
    public bool MaskActive { get; set; }

    /// <summary>Discrete intensity level 1..12 (the native intensity scale that
    /// replaced the legacy VimsTier 1..10 — Adaptive Fit Layer Q2=B). Updated by
    /// the engine with hysteresis + asymmetric debounce so the displayed mask is
    /// stable within a level. Property name kept as <c>VimsTier</c> for now to
    /// bound the rebuild's blast radius; the user-facing label is the 5級 動態感
    /// projection, not this raw value.</summary>
    public int VimsTier { get; set; } = 1;

    /// <summary>Alpha-2 (selector→renderer): when &gt;= 0, the peak mask alpha (0..1) the
    /// OverlayWindow must render for the CURRENT tier, resolved from the ACTIVE AdaptiveFit
    /// set's curve — so the selector's chosen mask DEPTH actually reaches the screen instead
    /// of being computed-but-ignored. -1 (the default) means "no override": the overlay uses
    /// its static tier-keyed VimsLevels alpha, byte-identical to pre-Alpha-2 / flag-OFF. Only
    /// the depth is set-driven; the hue/colour stays tier-keyed in the overlay. set0 resolves
    /// to exactly the VimsLevels alpha (pinned drift test), so the default set is unchanged.</summary>
    public double MaskPeakAlphaOverride { get; set; } = -1.0;

    /// <summary>"Driver" or "Passenger" — affects passive optical-flow weighting.</summary>
    public string Mode { get; set; } = "Passenger";

    /// <summary>Foreground application name (MVP: a fixed demo value).</summary>
    public string ForegroundApp { get; set; } = "Demo";

    /// <summary>Fast Motion Sickness score (0–20 style scale).</summary>
    public int FmsScore { get; set; }

    /// <summary>Opacity of the static anchor grid in the overlay.</summary>
    public double GridOpacity { get; set; }

    /// <summary>Grid cell size in pixels.</summary>
    public int GridSizePx { get; set; }

    /// <summary>Normalized overlay render knobs adjusted by chatbot feedback.</summary>
    public ShaderParams Shader { get; set; } = ShaderParams.Default;

    /// <summary>Whether gaze / eye-tracking input is enabled (stub for now).</summary>
    public bool EyeTrackingEnabled { get; set; }

    /// <summary>Whether the Habituation Engine is paused.</summary>
    public bool HabituationPaused { get; set; }

    /// <summary>Re-habituation progress, 0 (full overlay reliance) to 100 (re-adapted to baseline).</summary>
    public int HabituationLevel { get; set; }

    /// <summary>Timestamp of the most recent inference update.</summary>
    public DateTime LastUpdatedAt { get; set; }

    // ── M3 head-tracking sprint: head-pose-driven FOV mask centering ─────────
    // These three properties carry the smoothed normalized head-gaze sample
    // from IHeadPoseReader (M4 wiring) through to OverlayWindow's mask draw.
    // Defaults match the v0.9 baseline (centered mask, tracking inactive) so
    // an unconfigured state is byte-equivalent to pre-M3 behavior. SenseState
    // is a `class` (not a `record`) in this codebase, so these match the
    // existing `{ get; set; }` style. Engine MUST NOT read these as input —
    // they're forward-only display knobs, mutated by the head-pose consumer
    // before each Apply pass.

    /// <summary>Normalized head-gaze X (0.0 left edge .. 1.0 right edge).
    /// Default 0.5 (center) so flag-off path is parameter-identical to v0.9.</summary>
    public double HeadGazeX { get; set; } = 0.5;

    /// <summary>Normalized head-gaze Y (0.0 top edge .. 1.0 bottom edge).
    /// Default 0.5 (center) so flag-off path is parameter-identical to v0.9.</summary>
    public double HeadGazeY { get; set; } = 0.5;

    /// <summary>True iff IHeadPoseReader produced a fresh sample on the most
    /// recent inference tick (sidecar up + face detected + confidence ok).
    /// Default false. When false, consumers MUST fall back to the (0.5, 0.5)
    /// center default — never trust HeadGazeX/Y when this is false.</summary>
    public bool HeadTrackingActive { get; set; } = false;
}
