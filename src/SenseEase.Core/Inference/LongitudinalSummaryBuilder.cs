using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

/// <summary>
/// UM5 (User Model sprint): builds the <c>[behavior_trend]</c> block injected into the
/// chatbot prompt AFTER <c>[habituation_state]</c>. Summarises the last 30 days of
/// <c>behavior_log.jsonl</c> aggregates into five terse trend fields so the LLM can
/// ground replies in the user's longer-term exposure / adaptation arc without
/// re-deriving it each turn. Target budget: ~80 tokens.
///
/// <para>Returns an empty string (no block injected) when fewer than
/// <see cref="MinDaysForSummary"/> days of data exist — too little history to
/// summarise meaningfully.</para>
/// </summary>
public static class LongitudinalSummaryBuilder
{
    public const int WindowDays = 30;
    public const int MinDaysForSummary = 7;

    public static string BuildBlock(IReadOnlyList<DailyBehaviorAggregate>? recentRows)
    {
        if (recentRows is null || recentRows.Count == 0) return string.Empty;

        // behavior_log is one row per day; use at most the last WindowDays.
        var rows = recentRows.Count > WindowDays
            ? recentRows.Skip(recentRows.Count - WindowDays).ToList()
            : new List<DailyBehaviorAggregate>(recentRows);

        if (rows.Count < MinDaysForSummary) return string.Empty;

        var sb = new StringBuilder(220);
        sb.Append("[behavior_trend]\n");
        sb.Append("window: ").Append(WindowDays).Append("d\n");
        sb.Append("exposure_days: ").Append(rows.Count).Append('/').Append(WindowDays).Append('\n');
        sb.Append("ofi_p90_trend: ").Append(OfiP90Trend(rows)).Append('\n');
        sb.Append("adaptation_slope: ").Append(AdaptationSlope(rows)).Append('\n');
        sb.Append("spike_rate: ").Append(SpikeRate(rows)).Append('\n');
        sb.Append("tolerance_ceiling: ").Append(ToleranceCeiling(rows)).Append('\n');
        sb.Append("[/behavior_trend]");
        return sb.ToString();
    }

    private static (List<DailyBehaviorAggregate> early, List<DailyBehaviorAggregate> late) Halves(
        List<DailyBehaviorAggregate> rows)
    {
        int half = rows.Count / 2;
        return (rows.Take(half).ToList(), rows.Skip(half).ToList());
    }

    private static string OfiP90Trend(List<DailyBehaviorAggregate> rows)
    {
        var (early, late) = Halves(rows);
        double e = early.Count > 0 ? early.Average(r => r.OfiP90) : 0;
        double l = late.Count > 0 ? late.Average(r => r.OfiP90) : 0;
        int delta = (int)Math.Round(l - e);
        string dir = delta > 2 ? "rising" : delta < -2 ? "falling" : "steady";
        return $"{(delta >= 0 ? "+" : "")}{delta} ({dir})";
    }

    private static string AdaptationSlope(List<DailyBehaviorAggregate> rows)
    {
        double totalDelta = rows.Sum(r => r.OffsetDeltaPp);
        double weeks = Math.Max(1.0, rows.Count / 7.0);
        double slope = totalDelta / weeks;
        return $"{(slope >= 0 ? "+" : "")}{slope.ToString("0.0", CultureInfo.InvariantCulture)}pp/week";
    }

    private static string SpikeRate(List<DailyBehaviorAggregate> rows)
    {
        var (early, late) = Halves(rows);
        double e = early.Count > 0 ? early.Average(r => r.SpikeCount) : 0;
        double l = late.Count > 0 ? late.Average(r => r.SpikeCount) : 0;
        string dir = l < e - 0.05 ? "improving" : l > e + 0.05 ? "worsening" : "steady";
        return $"{e.ToString("0.0", CultureInfo.InvariantCulture)}/day → " +
               $"{l.ToString("0.0", CultureInfo.InvariantCulture)}/day ({dir})";
    }

    private static string ToleranceCeiling(List<DailyBehaviorAggregate> rows)
    {
        int ceiling = rows.Max(r => r.TierModal);
        // "stable" when the trailing week sat within one tier of the ceiling.
        int trailing = Math.Min(7, rows.Count);
        bool stable = rows.Skip(rows.Count - trailing).All(r => r.TierModal >= ceiling - 1);
        return stable ? $"tier {ceiling} (stable)" : $"tier {ceiling}";
    }
}
