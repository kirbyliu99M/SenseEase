using System.Collections.Generic;
using System.Text.Json.Serialization;
using SenseEase.Core.Inference;

namespace SenseEase.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// JSON schema v2 for habituation_profile.json.
//
// This is the CONTRACT with the chatbot prompt-injection layer (Phase 6) and
// any future external reader. Field names are snake_case per spec; the writer
// uses JsonNamingPolicy.SnakeCaseLower OR explicit [JsonPropertyName]
// attributes — both belt and suspenders so a future serializer-config change
// can't silently rewrite field names. Schema mutations require bumping
// SchemaVersion AND updating docs/HABITUATION-PROFILE.md §5.
//
// Nullable fields: last_spike_at_iso, next_step_eligible_at_iso,
// last_calibration_at_iso, and the calibration_* enum properties. ISO-8601
// strings (UTC, "o" format). Null means "never happened" / "not applicable".
//
// v2 (Stage 6): adds calibration_* fields, longitudinal_observations,
// personal_pressure_scale, symptom_dominance_primary, medical_advisory_shown,
// calibration_confidence, and was_recovered_from_wipe. v1 files load as v2
// in-memory with the new fields at their CLR defaults (see
// FileHabituationProfileStore.LoadLegacyV1AsV2InMemory). The on-disk file is
// upgraded to v2 the next time the snapshot is written.
//
// Enum properties serialize as snake_case strings via a global
// JsonStringEnumConverter on AtomicJsonWriter's options (e.g. "complete",
// "buttons", "high_risk_combo").
//
// See docs/HABITUATION-PROFILE.md §5 for the full schema reference + the
// schema-mismatch fall-back policy (Q2: silent fallback to RESILIENT +
// System.Diagnostics.Debug.WriteLine for truly unknown versions).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Persistence snapshot for the habituation profile state. v2 schema.</summary>
public sealed class HabituationProfileSnapshot
{
    /// <summary>Currently 3 (UM2). Loader accepts 3 directly, migrates 2 → 3
    /// (flat longitudinal_observations → current-month bucket), and treats 1 as
    /// "v1 file → load with v2 defaults" (unchanged). Unknown versions fall back
    /// to null + Debug.WriteLine (Q2).</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 3;

    /// <summary>ISO-8601 UTC ("o" format) — when the file was last written.</summary>
    [JsonPropertyName("updated_at_iso")]
    public string UpdatedAtIso { get; set; } = string.Empty;

    /// <summary>"RESILIENT" / "TYPICAL" / "SENSITIVE".</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "RESILIENT";

    /// <summary>"default" / "calibration_demo" / "chatbot_override".</summary>
    [JsonPropertyName("profile_source")]
    public string ProfileSource { get; set; } = "default";

    /// <summary>0–100. Habituation progress as a fraction of the profile's cap.</summary>
    [JsonPropertyName("habituation_level")]
    public double HabituationLevel { get; set; }

    /// <summary>Current FOV-relaxation offset in percentage points.</summary>
    [JsonPropertyName("habituation_offset_pp")]
    public double HabituationOffsetPp { get; set; }

    /// <summary>Per-profile cap on the offset. Surfaced so chatbot can reason
    /// about headroom without consulting the profile lookup separately.</summary>
    [JsonPropertyName("habituation_offset_cap_pp")]
    public int HabituationOffsetCapPp { get; set; }

    /// <summary>Current VIMS tier (1–10) as seen by the engine on its most
    /// recent Step. Stale by at most one tick on read.</summary>
    [JsonPropertyName("current_vims_tier")]
    public int CurrentVimsTier { get; set; }

    /// <summary>Weighted strike count (sensor=1.0, chatbot=0.5).</summary>
    [JsonPropertyName("severe_strike_count")]
    public double SevereStrikeCount { get; set; }

    /// <summary>Active profile's strike limit.</summary>
    [JsonPropertyName("severe_strike_limit")]
    public double SevereStrikeLimit { get; set; }

    /// <summary>Phase 7 write-durability: a user-confirmed chatbot AdjustStrikeLimit override
    /// (range [1,5]), persisted so it survives a restart instead of reverting to the profile
    /// default. null = no override (use the profile default). Additive nullable field; schema
    /// stays back-compat (older files deserialize to null). NOTE: the vestibular hard-cap (=1)
    /// is NOT carried here — it is re-derived from <see cref="VestibularProtectionEnabled"/> on
    /// load and intentionally WINS over this override.</summary>
    [JsonPropertyName("severe_strike_limit_override")]
    public int? SevereStrikeLimitOverride { get; set; }

    /// <summary>"RUNNING" / "COOLDOWN" / "PAUSED_AWAITING_USER_CONFIRM".</summary>
    [JsonPropertyName("engine_state")]
    public string EngineState { get; set; } = "RUNNING";

    /// <summary>Parallel to engine_state. Q8 supplement: external pause does
    /// NOT mask the underlying engine_state; chatbot sees both facts.</summary>
    [JsonPropertyName("externally_paused")]
    public bool ExternallyPaused { get; set; }

    /// <summary>Convenience boolean — true iff engine_state ==
    /// PAUSED_AWAITING_USER_CONFIRM. Duplicates information but lets chatbot
    /// branch on a single flag.</summary>
    [JsonPropertyName("awaiting_user_confirm")]
    public bool AwaitingUserConfirm { get; set; }

    /// <summary>ISO-8601 UTC. Null if no spike has been recorded yet.</summary>
    [JsonPropertyName("last_spike_at_iso")]
    public string? LastSpikeAtIso { get; set; }

    /// <summary>ISO-8601 UTC. Estimated time of the next relaxation step.
    /// Null when at cap OR paused.</summary>
    [JsonPropertyName("next_step_eligible_at_iso")]
    public string? NextStepEligibleAtIso { get; set; }

    /// <summary>Snapshot of the active profile's tuning. Lets the chatbot
    /// reason about cadence without resolving HabituationProfile statics.</summary>
    [JsonPropertyName("params_snapshot")]
    public HabituationParamsSnapshot ParamsSnapshot { get; set; } = new();

    // ── v2 calibration fields (Stage 6) ──────────────────────────────────────
    // All optional / default-initialised. v1 files load with these at CLR
    // defaults; the in-memory snapshot is v2-shaped after Load returns, the
    // on-disk file is upgraded to v2 on the next Save.

    /// <summary>Total number of calibration sessions the user has completed
    /// (Complete or Partial — Abandoned runs do NOT increment per spec §3).
    /// Drives the "first calibration" vs "longitudinal re-calibration" branch
    /// in CalibrationProcessor.</summary>
    [JsonPropertyName("calibration_count")]
    public int CalibrationCount { get; set; }

    /// <summary>ISO-8601 UTC ("o" format). Null = no calibration has completed
    /// yet. Independent of <see cref="UpdatedAtIso"/>, which advances on every
    /// snapshot write (engine tick, profile change, etc.).</summary>
    [JsonPropertyName("last_calibration_at_iso")]
    public string? LastCalibrationAtIso { get; set; }

    /// <summary>Completeness tag for the most recent calibration session.
    /// Null = no calibration has ever run.</summary>
    [JsonPropertyName("calibration_completeness")]
    public CalibrationCompleteness? CalibrationCompleteness { get; set; }

    /// <summary>Which path produced the most recent calibration (buttons / LLM
    /// / LLM-with-button-fallback). Null = no calibration has ever run.</summary>
    [JsonPropertyName("calibration_path")]
    public CalibrationPath? CalibrationPath { get; set; }

    /// <summary>Verbatim user answers from the most recent calibration. Null =
    /// no calibration has ever run. Carries source tags per field per spec §8
    /// so downstream readers can reason about confidence.</summary>
    [JsonPropertyName("calibration_responses")]
    public CalibrationResponses? CalibrationResponses { get; set; }

    /// <summary>DEPRECATED in v3 (UM2). The flat global FIFO of v2. Retained only
    /// so a v2 file can be read + migrated into <see cref="LongitudinalObservationsV2"/>
    /// and for back-compat reads; the writer no longer appends here. Empty on a
    /// freshly-written v3 snapshot.</summary>
    [JsonPropertyName("longitudinal_observations")]
    public List<string> LongitudinalObservations { get; set; } = new();

    /// <summary>UM2 (schema v3): longitudinal observations bucketed by month
    /// ("YYYY-MM" → entries). Replaces the flat v2 FIFO: 12 months retained,
    /// 30 entries per bucket, dedup scoped to the same month. A v2 file's flat
    /// entries are migrated into the current month's bucket on load.</summary>
    [JsonPropertyName("longitudinal_observations_v2")]
    public Dictionary<string, List<ObservationEntry>> LongitudinalObservationsV2 { get; set; } = new();

    /// <summary>Multiplicative pressure scale per spec §7. Clamped to
    /// [0.7, 1.5] by CalibrationProcessor; vestibular-diagnosed users hard-
    /// capped to 1.0. Default 1.0 = "no personalisation yet" (RESILIENT
    /// behaviour). Applied as <c>effectiveTrigger = defaultTrigger / scale</c>
    /// at the engine boundary; engine itself stays ignorant of "scale".</summary>
    [JsonPropertyName("personal_pressure_scale")]
    public double PersonalPressureScale { get; set; } = 1.0;

    /// <summary>B5 (learning loop): the deterministic AdaptationGate's signed contribution to the engine
    /// EffectiveTrigger, in pressure points (negative = tighten/more protection, positive = relax/less
    /// protection). A DEDICATED auto-channel, separate from <see cref="PersonalPressureScale"/> (the
    /// user/load channel); composed as <c>effectiveTrigger = base + this</c> and re-applied at boot.
    /// Default 0.0 = no auto-adaptation, so pre-B5 snapshots deserialize to 0 ⇒ byte-identical behaviour.
    /// Zeroed by a user Reset so an auto-relax can never survive a Reset.</summary>
    [JsonPropertyName("auto_adapt_trigger_delta_pp")]
    public double AutoAdaptTriggerDeltaPp { get; set; } = 0.0;

    /// <summary>SSQ-style primary symptom dimension from the most recent
    /// calibration. Null = no symptoms reported (all three SSQ dimension
    /// scores zero) OR no calibration has run.</summary>
    [JsonPropertyName("symptom_dominance_primary")]
    public SymptomDominance? SymptomDominancePrimary { get; set; }

    /// <summary>Which medical advisory (if any) has been shown to the user.
    /// Persisted across runs so the "show at most once per kind per user"
    /// rule survives restarts (spec §6). Null = no advisory has fired yet.</summary>
    [JsonPropertyName("medical_advisory_shown")]
    public MedicalAdvisory? MedicalAdvisoryShown { get; set; }

    /// <summary>Overall confidence in the most recent calibration per spec §8.
    /// Null = no calibration has run.</summary>
    [JsonPropertyName("calibration_confidence")]
    public CalibrationConfidence? CalibrationConfidence { get; set; }

    /// <summary>Alpha-2 addendum (cancel explicit calibration → auto-profiling):
    /// persistent vestibular-protection signal for the auto-profiling world, where
    /// the cancelled calibration Q5 no longer sets it. Written by
    /// <c>MockInferenceEngine.SetVestibularProtection</c> (driven by the one-time
    /// first-use field + the permanent Settings toggle); read back at engine load to
    /// re-apply the L9 floor / TriggerPressure-50 / SevereStrikeLimit-1 protection
    /// across restarts. Additive (schema stays v3); default <c>false</c>, so existing
    /// calibration-era snapshots are unaffected and the legacy
    /// <c>CalibrationResponses.Q5RiskFactors</c> vestibular path is unchanged.</summary>
    [JsonPropertyName("vestibular_protection_enabled")]
    public bool VestibularProtectionEnabled { get; set; }

    /// <summary>True iff this snapshot was synthesized by the loader because
    /// habituation_profile.json was missing but other persistence artifacts
    /// (e.g. habituation.json) indicate prior app usage. Signals to upstream
    /// code "not a true first-run; user's profile was wiped". The flag is
    /// set by the loader at read time and is NOT persisted on subsequent
    /// writes — the next Save emits this field as false. See
    /// FileHabituationProfileStore.Load for the detection logic.</summary>
    [JsonPropertyName("was_recovered_from_wipe")]
    public bool WasRecoveredFromWipe { get; set; }
}

/// <summary>UM2 (schema v3): one longitudinal observation inside a monthly bucket.
/// <c>ts</c> is the ISO-8601 UTC capture time; <c>text</c> is the (already
/// write-time-sanitized) observation body.</summary>
public sealed class ObservationEntry
{
    [JsonPropertyName("ts")]
    public string Ts { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>Sub-record nested inside <see cref="HabituationProfileSnapshot.ParamsSnapshot"/>.
/// Surfaces the three time/magnitude knobs the chatbot is most likely to
/// reason about ("how long until the next step?", "how big is each step?").
/// Other profile fields are derivable from the <c>profile</c> enum string and
/// don't need duplicating into the snapshot.</summary>
public sealed class HabituationParamsSnapshot
{
    [JsonPropertyName("stable_window_ms")]
    public int StableWindowMs { get; set; }

    [JsonPropertyName("step_interval_ms")]
    public int StepIntervalMs { get; set; }

    [JsonPropertyName("step_magnitude_pp")]
    public double StepMagnitudePp { get; set; }
}
