using System;
using System.Collections.Generic;
using System.Linq;

namespace SenseEase.Core.Inference;

/// <summary>
/// Learning-loop Part D, Idea #2 (2026-06-21): one per-launch record of the FIRST discomfort signal of a
/// session — the earliest of (a) the user accepting the overlay-consent card, (b) a manual FMS check-in,
/// or (c) a chatbot turn the NLU classifies as <c>ReportDiscomfort</c>. Captured by the App's
/// <c>SessionOnsetLog</c> service (one row per app-launch, first-of-session wins) and aggregated here into
/// the "typical time to first discomfort" distribution.
///
/// <para><see cref="OffsetSeconds"/> is an ELAPSED DURATION (UtcNow − session-start), NOT a wall-clock
/// timestamp — so the report-side aggregate (mean minutes) honours D-17's "no finer-than-date
/// timestamp" rule. The session anchor is app-launch (the only fixed wall-clock origin).</para>
///
/// <para><b>Safety fence (see docs/LEARNING_LOOP_PLAN.md §D2).</b> This timing is REPORT-ONLY. It must
/// NEVER be assembled into <c>AdaptationGate.MeasuredState</c> or read by the relax gate — it carries no
/// FMS value and no protection semantics. It may later inform check-in/prompt CADENCE (a governor input,
/// behind its own UVHS); it must never set a protection delta.</para>
/// </summary>
public sealed class SessionOnsetRecord
{
    /// <summary>Which channel fired first this session: see <see cref="SessionOnsetChannels"/>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Elapsed seconds from session start (app launch) to the first signal. A duration, not a
    /// timestamp (D-17-safe).</summary>
    public double OffsetSeconds { get; set; }
}

/// <summary>The three first-touch channels (Idea #2 clauses a/b/c). All are genuine USER discomfort
/// signals: consent-accept, FMS check-in, and an NLU ReportDiscomfort chat turn.</summary>
public static class SessionOnsetChannels
{
    public const string Consent = "consent";
    public const string Fms     = "fms";
    public const string Chat    = "chat";
}

/// <summary>Mean time-to-first-signal for one channel (or overall). Minutes, a duration (D-17-safe).</summary>
public sealed class ChannelOnset
{
    public string Channel { get; set; } = string.Empty;
    /// <summary>Number of sessions whose first signal came via this channel.</summary>
    public int SessionCount { get; set; }
    /// <summary>Mean minutes from session start to the first signal on this channel. NULL when no
    /// session used this channel (honest gap, never a fabricated 0).</summary>
    public double? MeanMinutes { get; set; }
}

/// <summary>The de-identified "typical onset timing" summary: overall + per-channel means.</summary>
public sealed class SessionFirstSignalSummary
{
    /// <summary>Total sessions that recorded a first signal.</summary>
    public int SessionCount { get; set; }
    /// <summary>Mean minutes to first signal across ALL channels — the headline "typical time before a
    /// user starts facing discomfort". NULL when there is no data.</summary>
    public double? MeanMinutesOverall { get; set; }
    /// <summary>Per-channel breakdown (consent / fms / chat), each with its own mean + support. Only
    /// channels with at least one session appear.</summary>
    public List<ChannelOnset> ByChannel { get; set; } = new();
}

/// <summary>
/// Pure aggregator for <see cref="SessionFirstSignalSummary"/>. Takes the per-session onset records and
/// returns the de-identified mean-minutes distribution — no I/O, no clock. Mirrors
/// <see cref="ClinicalExportBuilder"/>'s pure-builder shape; only finite, non-negative offsets count.
/// </summary>
public static class SessionFirstSignalBuilder
{
    public static SessionFirstSignalSummary Build(IEnumerable<SessionOnsetRecord> records)
    {
        var valid = (records ?? Enumerable.Empty<SessionOnsetRecord>())
            .Where(r => r is not null
                        && !string.IsNullOrWhiteSpace(r.Channel)
                        && double.IsFinite(r.OffsetSeconds)
                        && r.OffsetSeconds >= 0)
            .ToList();

        var summary = new SessionFirstSignalSummary
        {
            SessionCount = valid.Count,
            MeanMinutesOverall = MeanMinutes(valid.Select(r => r.OffsetSeconds)),
            ByChannel = valid
                .GroupBy(r => r.Channel, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ChannelOnset
                {
                    Channel      = g.Key,
                    SessionCount = g.Count(),
                    MeanMinutes  = MeanMinutes(g.Select(r => r.OffsetSeconds)),
                })
                .ToList(),
        };
        return summary;
    }

    /// <summary>Arithmetic mean of the offsets, converted to minutes, rounded to 2dp. Null on an empty set.</summary>
    internal static double? MeanMinutes(IEnumerable<double> offsetSeconds)
    {
        var vals = offsetSeconds.Where(double.IsFinite).ToList();
        if (vals.Count == 0) return null;
        return Math.Round(vals.Average() / 60.0, 2, MidpointRounding.AwayFromZero);
    }
}
