namespace SenseEase.Core.Models;

/// <summary>Result of interpreting a user feedback utterance (mock NLU for MVP).</summary>
public class NluResult
{
    /// <summary>Detected intent, e.g. "ReportDiscomfort", "ReportComfort", "ResetRequest", "Unknown".</summary>
    public string Intent { get; set; } = "Unknown";

    /// <summary>Suggested change to the FOV radius percent (negative = shrink).</summary>
    public int FovDelta { get; set; }

    /// <summary>Suggested delta to ShaderParams.MaskDepth (normalized, clamped [-0.3,0.3]).</summary>
    public double MaskDepthDelta { get; set; }

    /// <summary>Suggested delta to ShaderParams.GridVisibility (normalized, clamped [-0.3,0.3]).</summary>
    public double GridVisibilityDelta { get; set; }

    /// <summary>Suggested delta to ShaderParams.FeatherWidth (normalized, clamped [-0.3,0.3]).</summary>
    public double FeatherWidthDelta { get; set; }

    /// <summary>Human-readable response shown back to the user.</summary>
    public string Response { get; set; } = string.Empty;
}
