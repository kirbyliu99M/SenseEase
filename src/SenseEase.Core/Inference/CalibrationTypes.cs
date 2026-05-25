namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// Calibration types — concrete records used by CalibrationProcessor.
//
// Pure logic surface. NO JSON serialization concerns here — the snapshot DTO
// in Core.Models.HabituationProfileSnapshot (Stage 6) maps these to/from the
// wire format with explicit string conversions. Keeping the working types
// strongly-enum'd here lets the C# compiler catch most calibration-logic bugs
// before runtime.
//
// See docs/CALIBRATION.md (Stage 7) for the user-facing question text + the
// product rationale for each field. This file is the engineering contract.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Q1 — which segment of the demo felt worst. <see cref="None"/> is
/// the explicit "全程都還好 / nothing felt bad" answer (distinct from "user
/// didn't answer" which is represented by a null <see cref="CalibrationResponses.Q1Segment"/>).</summary>
public enum UncomfortableSegment
{
    Start,
    Middle,
    End,
    None,
}

/// <summary>SSQ-aligned dominance category for <see cref="CalibrationResult.SymptomDominancePrimary"/>.
/// <see cref="Mixed"/> = multiple axes tied at the top.
/// Property type is nullable; <c>null</c> = no symptoms reported (all three
/// dimension scores zero), distinct from <see cref="Mixed"/>.
/// Serialized to JSON as snake_case strings by the Stage 6 snapshot DTO.</summary>
public enum SymptomDominance
{
    Nausea,
    Oculomotor,
    Disorientation,
    Mixed,
}

/// <summary>Overall reliability of a calibration session.
/// Non-nullable — a calibration that produced a result always has a confidence
/// level (worst case: <see cref="Low"/> on an Abandoned run).
/// Serialized to JSON as snake_case strings.</summary>
public enum CalibrationConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>Q2 — symptom dominance vocabulary. Mirrors the SSQ three-dimension
/// structure (Kennedy et al. 1993) at the user-visible layer:
/// nausea + oculomotor + disorientation groupings happen in
/// <see cref="CalibrationProcessor"/> when computing
/// <see cref="CalibrationResult.SymptomDominancePrimary"/>.</summary>
public enum SymptomType
{
    Dizziness,
    Nausea,
    EyeStrain,
    Headache,
    Imbalance,
    Disorientation,
}

/// <summary>Q4 — comparison to the user's typical YouTube/TikTok/game baseline.</summary>
public enum BaselineComparison
{
    Worse,
    Similar,
    Better,
}

/// <summary>Q5 — long-term risk factors. <see cref="None"/> and <see cref="Skipped"/>
/// are distinct: <see cref="None"/> means "user reviewed the list and reported
/// nothing applies"; <see cref="Skipped"/> means "user declined to share". They
/// drive different downstream behaviours (Skipped → no escalation, no advisory;
/// None → trusted baseline for future delta detection).</summary>
public enum RiskFactor
{
    MotionSickness,
    Migraine,
    VestibularDiagnosed,
    VrSusceptible,
    UnexplainedDizziness,
    None,
    Skipped,
}

/// <summary>Source-of-signal tag for each calibration answer. Drives downstream
/// confidence weighting. The string values exactly match the JSON
/// <c>_source</c> field values per spec §8.</summary>
public enum CalibrationSource
{
    /// <summary>Answer came from the 5-card buttons UI (first calibration).</summary>
    Buttons,
    /// <summary>Answer came from the LLM dialog, confidence ≥ 0.7 (or ≥ 0.5 for non-Q3 fields).</summary>
    Llm,
    /// <summary>Answer came from the LLM dialog, confidence below the threshold.
    /// The value is still recorded but the source tag warns downstream consumers
    /// it's noisier.</summary>
    LlmLowConfidence,
    /// <summary>Q3-only: LLM dialog failed to reach 0.7 confidence on intensity,
    /// so the system fell back to showing Card 3 alone and got a button answer.</summary>
    FallbackButtons,
}

/// <summary>Completion state of a calibration session. Persisted alongside the
/// answers so future analysis can distinguish trustworthy completes from rushed
/// partial answers.</summary>
public enum CalibrationCompleteness
{
    /// <summary>All 5 questions answered (each has a non-null primary value).</summary>
    Complete,
    /// <summary>Q3 (intensity) answered but at least one other question left null.
    /// Calibration counts toward <c>calibration_count</c>.</summary>
    Partial,
    /// <summary>Q3 NOT answered — user closed the dialog before the mandatory
    /// intensity question. Calibration does NOT count; system reverts to
    /// RESILIENT default; next demo end re-triggers first-time calibration.</summary>
    Abandoned,
}

/// <summary>Per-question answers. Nullable primary values represent "user did
/// not answer" (only Q3 is mandatory per spec §3); freetext is always
/// independently optional. Lists use null for "didn't answer" and empty list
/// for "explicitly chose 'none'" (only meaningful for Q2 and Q5).</summary>
public sealed record CalibrationResponses
{
    // Q1 — uncomfortable segment localisation.
    // NOTE: the Q1SignalConflict signal (user picked "全程都還好" but freetext
    // mentions a symptom) is NOT stored on this record. It's DERIVED by
    // CalibrationProcessor.DetectQ1SignalConflict so both packing paths
    // (card UI + LLM parser) share one definition. The boolean lives on
    // CalibrationResult.Q1SignalConflict instead.
    public UncomfortableSegment? Q1Segment { get; init; }
    public string? Q1Freetext { get; init; }

    // Q2 — symptom dominance (multi-select)
    /// <summary>Null = user didn't answer. Empty list = user explicitly chose
    /// "都沒有 / no symptoms" — informative; treated as zero on every SSQ
    /// dimension.</summary>
    public IReadOnlyList<SymptomType>? Q2Symptoms { get; init; }
    public string? Q2Freetext { get; init; }

    // Q3 — intensity (mandatory, 1–5)
    /// <summary>Required for a non-Abandoned completion. Null means the user
    /// closed the dialog before answering Q3 → calibration is Abandoned.</summary>
    public int? Q3Intensity { get; init; }
    public string? Q3Freetext { get; init; }

    // Q4 — baseline comparison
    public BaselineComparison? Q4Comparison { get; init; }
    /// <summary>True iff user explicitly chose "我平常不看這類動態畫面 / I don't
    /// usually watch this kind of content". Independent flag because the
    /// comparison itself is meaningless in that case.</summary>
    public bool Q4NoBaseline { get; init; }
    public string? Q4Freetext { get; init; }

    // Q5 — risk factors (multi-select)
    /// <summary>Null = user didn't answer. List containing only
    /// <see cref="RiskFactor.None"/> = explicit "no risk factors". List
    /// containing only <see cref="RiskFactor.Skipped"/> = explicit
    /// "decline to share".</summary>
    public IReadOnlyList<RiskFactor>? Q5RiskFactors { get; init; }
    public string? Q5Freetext { get; init; }

    // ── Source tags (per field) ──────────────────────────────────────────────
    // Populated by the calibration path (buttons vs LLM). Default Buttons so
    // the first-card path can omit them; LLM path sets explicitly.
    public CalibrationSource Q1Source { get; init; } = CalibrationSource.Buttons;
    public CalibrationSource Q2Source { get; init; } = CalibrationSource.Buttons;
    public CalibrationSource Q3Source { get; init; } = CalibrationSource.Buttons;
    public CalibrationSource Q4Source { get; init; } = CalibrationSource.Buttons;
    public CalibrationSource Q5Source { get; init; } = CalibrationSource.Buttons;
}

/// <summary>Sensor observations captured during the demo run that wraps the
/// calibration session. Fed into CalibrationProcessor alongside the user's
/// answers so the personal_pressure_scale lookup has both axes.</summary>
public sealed record CalibrationSensorData(
    double PeakPressure,
    int MaskActivations,
    double PeakPressureAtSec);

/// <summary>What CalibrationProcessor produces. All fields together drive the
/// downstream <see cref="HabituationEngine.SetProfile"/> call AND the personal-
/// trigger plumbing in <c>MockInferenceEngine</c>.</summary>
public sealed record CalibrationResult
{
    /// <summary>Active profile after applying Q3+Q4 base mapping + Q5 escalation.</summary>
    public required HabituationProfile Profile { get; init; }

    /// <summary>Personal pressure scale (the table in spec §7).
    /// Clamped to [0.7, 1.5]; vestibular-diagnosed users hard-capped to 1.0.
    /// Applied at the engine boundary as <c>effectiveTrigger = defaultTrigger / scale</c>
    /// — engine itself stays ignorant of "scale".</summary>
    public required double PersonalPressureScale { get; init; }

    /// <summary>Direct trigger-pressure override that bypasses the scale path.
    /// Currently only set by the vestibular-diagnosed branch (= 50). When non-null,
    /// the wrapper uses this verbatim as the engine's EffectiveTrigger and ignores
    /// the scale.</summary>
    public int? TriggerThresholdOverride { get; init; }

    /// <summary>Additive adjustment to the default trigger pressure. Currently
    /// only set by the single-risk-factor branch (= -10). Applied AFTER the
    /// scale division when no override is in effect.</summary>
    public int TriggerThresholdAdjustment { get; init; }

    /// <summary>Override on the profile's SevereStrikeLimit. Only set by the
    /// vestibular-diagnosed branch (= 1). Null = use the profile's default.</summary>
    public int? SevereStrikeLimitOverride { get; init; }

    /// <summary>Which medical-advisory text (if any) to surface to the user.
    /// "vestibular" / "high_risk_combo" / null. Per spec §6, advisory shows
    /// at most once per advisory kind per user.</summary>
    public string? MedicalAdvisory { get; init; }

    /// <summary>SSQ-style primary dimension. Computed from Q2 via
    /// <see cref="CalibrationProcessor.ComputeSymptomDominancePrimary"/>.
    /// <c>null</c> = no symptoms reported / all three dimension scores zero.</summary>
    public SymptomDominance? SymptomDominancePrimary { get; init; }

    /// <summary>Overall confidence in this calibration per spec §8 rules.</summary>
    public required CalibrationConfidence CalibrationConfidence { get; init; }

    /// <summary>Conflict between Q1 segment and Q1 freetext (user picked
    /// "全程都還好" but mentioned a symptom keyword in freetext). Derived by
    /// <see cref="CalibrationProcessor.DetectQ1SignalConflict"/> at processing
    /// time so both packing paths (card UI + LLM parser) share one definition.
    /// Surfaced as <c>q1_signal_conflict</c> in the JSON snapshot.</summary>
    public bool Q1SignalConflict { get; init; }

    /// <summary>"complete" / "partial" / "abandoned" per spec §3.</summary>
    public required CalibrationCompleteness Completeness { get; init; }
}
