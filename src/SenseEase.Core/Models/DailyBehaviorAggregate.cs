namespace SenseEase.Core.Models;

/// <summary>
/// UM1 (User Model sprint): a daily rollup of engine telemetry, one object per day,
/// appended to <c>%LOCALAPPDATA%\SenseEase\behavior_log.jsonl</c>. Numeric aggregates
/// ONLY — no raw utterances, no OFI traces, no window titles (sprint no-PII
/// constraint; verify before merge). Lets the AI ground its replies in the user's
/// longer-term exposure / adaptation trend (UM5) without re-deriving it each turn.
///
/// <para>Schema v1 carries the sprint-locked PII annotations
/// <see cref="Exportable"/>=false and <see cref="DeletableOnClear"/>=true.</para>
/// </summary>
public sealed class DailyBehaviorAggregate
{
    /// <summary>The local calendar day this row aggregates, "YYYY-MM-DD". The
    /// idempotency key — one row per date.</summary>
    public string Date { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;
    public bool Exportable { get; set; } = false;
    public bool DeletableOnClear { get; set; } = true;

    /// <summary>App foreground minutes (exposure duration).</summary>
    public int SessionMinutes { get; set; }

    /// <summary>Median / 90th-percentile optical-flow pressure over the day.</summary>
    public int OfiP50 { get; set; }
    public int OfiP90 { get; set; }

    /// <summary>Most-frequent pressure tier (distribution mode).</summary>
    public int TierModal { get; set; }

    /// <summary>Severe-event (symptom-proxy) count for the day.</summary>
    public int SpikeCount { get; set; }

    /// <summary>Number of stable windows completed (successful habituation steps).</summary>
    public int StableWindowsCompleted { get; set; }

    /// <summary>Net habituation progress: offset(end) − offset(start) for the day, pp.</summary>
    public double OffsetDeltaPp { get; set; }

    /// <summary>FOV-mask activation (protection-trigger) count.</summary>
    public int MaskActivations { get; set; }
}
