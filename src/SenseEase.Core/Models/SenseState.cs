namespace SenseEase.Core.Models;

/// <summary>
/// Shared state model for the SenseEase core loop (Sensing → Inference → Rendering → Feedback).
/// Matches the spec in the design draft §9. All agents must conform to this contract.
/// </summary>
public class SenseState
{
    /// <summary>Raw / fused optical-flow index. MVP: driven by the dashboard slider.</summary>
    public double OpticalFlowIndex { get; set; }

    /// <summary>Accumulated visual-stimulus pressure from the Leaky Integrator.</summary>
    public double Pressure { get; set; }

    /// <summary>Inner clear-zone radius of the Chang-Hain FOV mask, in percent.</summary>
    public int FovRadiusPercent { get; set; }

    /// <summary>True once pressure has crossed the trigger threshold.</summary>
    public bool MaskActive { get; set; }

    /// <summary>Discrete VIMS tier 1..10. Updated by the engine with hysteresis +
    /// asymmetric debounce so the displayed mask is stable within a tier.</summary>
    public int VimsTier { get; set; } = 1;

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
}
