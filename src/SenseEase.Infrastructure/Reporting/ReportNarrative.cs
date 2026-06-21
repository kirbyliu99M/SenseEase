namespace SenseEase.Infrastructure.Reporting;

/// <summary>
/// Health-report redesign §8: the per-section plain-language narrative for the HTML report. Every string
/// here has ALREADY passed the two-wall pipeline (<c>ReplyComplianceScrubber.Scrub</c> →
/// <c>ReportClaimGuard.ContainsBannedClaim</c>) OR is the section's deterministic fallback template — so
/// the renderer can drop these in verbatim without further filtering. Sections are isolated: one model
/// failure or one rejected claim degrades only that section to its template, never the whole report.
/// </summary>
public sealed class ReportNarrative
{
    /// <summary>A 2–3 sentence plain-language opening (what this report covers).</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>One neutral gloss on the comfort-trend numbers (baseline vs recent, the per-day line).</summary>
    public string ComfortTrend { get; init; } = string.Empty;

    /// <summary>One neutral gloss on the protection ON-vs-OFF contrast + the current posture.</summary>
    public string Protection { get; init; } = string.Empty;

    /// <summary>One neutral gloss on exposure dose + on-screen-motion / mask-trigger frequency.</summary>
    public string Exposure { get; init; } = string.Empty;
}

/// <summary>Health-report redesign §8 (indicator 6): the de-identified protection posture. Carries ONLY
/// numbers + a coarse, neutral strength code — NO free text, NO calibration responses, NO longitudinal
/// observations (build-gate 1). The raw profile enum (RESILIENT / TYPICAL / SENSITIVE) is mapped to
/// <see cref="ProtectionStrength"/> at the service boundary so the report never emits "sensitive" /
/// "resilient" (both are <see cref="SenseEase.Core.Inference.ReportClaimGuard"/> stems).</summary>
public sealed class ReportPosture
{
    public ProtectionStrength Strength { get; init; }
    /// <summary>0–100 — habituation progress as a fraction of the profile cap. A raw number; the report
    /// labels it neutrally ("protection-relaxation progress"), never as user adaptation.</summary>
    public double HabituationLevel { get; init; }
    /// <summary>Current FOV-relaxation offset in percentage points.</summary>
    public double OffsetPp { get; init; }
    /// <summary>Per-profile cap on the offset, percentage points.</summary>
    public int OffsetCapPp { get; init; }
    /// <summary>Most-recent engine pressure tier (1–10).</summary>
    public int CurrentTier { get; init; }
}

/// <summary>A coarse, neutral de-identified rendering of the habituation profile's protection posture —
/// deliberately NOT the raw RESILIENT/TYPICAL/SENSITIVE enum (which would put a banned claim stem on a
/// user-facing surface). Light = least protection, Protective = most.</summary>
public enum ProtectionStrength
{
    Light,
    Balanced,
    Protective,
}
