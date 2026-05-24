using System.Text.Json.Serialization;

namespace SenseEase.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// JSON schema v1 for habituation_profile.json.
//
// This is the CONTRACT with the chatbot prompt-injection layer (Phase 6) and
// any future external reader. Field names are snake_case per spec; the writer
// uses JsonNamingPolicy.SnakeCaseLower OR explicit [JsonPropertyName]
// attributes — both belt and suspenders so a future serializer-config change
// can't silently rewrite field names. Schema mutations require bumping
// SchemaVersion AND updating docs/HABITUATION-PROFILE.md §5.
//
// Nullable fields: last_spike_at_iso, next_step_eligible_at_iso. ISO-8601
// strings (UTC, "o" format). Null means "never happened" / "not applicable".
//
// See docs/HABITUATION-PROFILE.md §5 for the full schema reference + the
// schema-mismatch fall-back policy (Q2: silent fallback to RESILIENT +
// System.Diagnostics.Debug.WriteLine).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Persistence snapshot for the habituation profile state. v1 schema.</summary>
public sealed class HabituationProfileSnapshot
{
    /// <summary>Locked to 1 in this v1 lifecycle. v1 readers refuse unknown
    /// versions and fall back to RESILIENT + default state.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

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
