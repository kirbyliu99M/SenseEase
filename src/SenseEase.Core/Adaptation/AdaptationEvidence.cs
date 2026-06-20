using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SenseEase.Core.Models;

namespace SenseEase.Core.Adaptation;

/// <summary>
/// B5 Stage 3 — the pure assembly of the <see cref="MeasuredState"/> the <see cref="AdaptationGate"/>
/// trusts. No I/O: the App reads the durable stores (symptom_log, behavior_log, adjustment_log) and
/// hands the materialised rows in; these functions do the safety-relevant aggregation.
///
/// <para>The two load-bearing guarantees live here: (1) the FMS corroboration is computed over MEASURED
/// reports only (<c>fms_report</c>) — chat-derived <c>nl_daily</c> estimates are excluded BY
/// CONSTRUCTION, so the relax evidence is un-injectable; (2) relaxation evidence is "positive evidence
/// of tolerated exposure" — OFI/session-minutes are the EXPOSURE (load) and spikes are the OUTCOME
/// (symptoms), so a declining-load/avoidance profile blocks relax instead of reading as "improving".</para>
/// </summary>
public static class AdaptationEvidence
{
    // ── FMS (measured-only) ──────────────────────────────────────────────────────────────────────

    public sealed record FmsEvidence(
        int OnReportCount,
        double WorstRecentFmsOn,
        bool CoverageOk,
        double HoursSinceLastDiscomfort,
        bool MeasuredDiscomfortInWindow);

    /// <summary>Aggregate the MEASURED, protection-ON FMS reports (B6 Stage 2). TWO horizons (red-team RT-1):
    /// <paramref name="countWindowDays"/> (e.g. 28d) drives REACHABILITY — the ON-report COUNT + coverage
    /// accumulate over it; <paramref name="recencyWindowDays"/> (≤ count, e.g. 10d) drives the worst-case,
    /// which is a true <b>MAX</b> (NOT a dilutable percentile) so no number of calm reports can bury a bad one.
    /// <c>HoursSinceLastDiscomfort</c> + <c>MeasuredDiscomfortInWindow</c> span the COUNT window (RT-3d), so a
    /// bad report that padded the count can never be invisible to suppression. Excludes nl_daily/estimate rows
    /// (the un-injectable invariant). Coverage = window-proportional spread (RT-2c). Reachability is bought
    /// from the count time-horizon ONLY; the worst-case + discomfort axes get STRICTER, never looser.</summary>
    public static FmsEvidence MeasuredOnFms(
        IEnumerable<SymptomEvent>? events, DateTime nowUtc,
        double countWindowDays, double recencyWindowDays, double discomfortThreshold)
    {
        var none = new FmsEvidence(0, 0.0, false, double.PositiveInfinity, false);
        if (events is null) return none;

        DateTime sinceCount   = nowUtc.AddDays(-countWindowDays);
        DateTime sinceRecency = nowUtc.AddDays(-recencyWindowDays);
        DateTime sinceLast7   = nowUtc.AddDays(-7);

        var onInCount   = new List<(DateTime ts, SymptomEvent e)>(); // ON-state, within the count window
        var onInRecency = new List<int>();                           // ON-state values, within the recency window
        double hoursSinceDiscomfort = double.PositiveInfinity;
        bool anyDiscomfortInWindow = false;

        foreach (var e in events)
        {
            if (e is null || !e.Measured || e.Kind != SymptomEvent.Kinds.FmsReport) continue; // MEASURED-ONLY
            if (!TryParseUtc(e.TsUtc, out DateTime ts) || ts < sinceCount) continue;          // within count window

            // Discomfort over the COUNT window (ANY intervention state): the clock + an "any-in-window" flag.
            // RT-3d: a ≥threshold report anywhere in the count window blocks relax — it cannot pad the count
            // and simultaneously be invisible to suppression (the dilution the worst-case MAX also guards).
            if (e.Value >= discomfortThreshold)
            {
                anyDiscomfortInWindow = true;
                double hrs = (nowUtc - ts).TotalHours;
                if (hrs >= 0 && hrs < hoursSinceDiscomfort) hoursSinceDiscomfort = hrs;
            }

            if (e.InterventionState == SymptomEvent.InterventionStates.On)
            {
                onInCount.Add((ts, e));
                if (ts >= sinceRecency) onInRecency.Add(e.Value);
            }
        }

        if (onInCount.Count == 0)
            return none with { HoursSinceLastDiscomfort = hoursSinceDiscomfort, MeasuredDiscomfortInWindow = anyDiscomfortInWindow };

        // Worst-case = MAX over the RECENCY ON-reports (RT-1: a max can't be diluted by calm reports). 0.0
        // when there are no ON-reports in the recency window — that "no recent data" case is caught by the
        // coverage recency-presence check below, never read as "comfortable".
        double worst = onInRecency.Count > 0 ? onInRecency.Max() : 0.0;

        // Window-proportional coverage (RT-2c): enough distinct days, no single day dominating the count, and
        // at least one ON-report in the last 7 days (an old calm streak cannot carry a stale relax).
        int distinctDays = onInCount.Select(x => x.ts.Date).Distinct().Count();
        int requiredDays = (int)Math.Ceiling(countWindowDays / 7.0);
        int maxPerDay = onInCount.GroupBy(x => x.ts.Date).Max(g => g.Count());
        bool coverageOk = distinctDays >= requiredDays
                       && maxPerDay * 2 <= onInCount.Count               // no single day > 50% of the count
                       && onInCount.Any(x => x.ts >= sinceLast7);        // recency presence

        return new FmsEvidence(
            OnReportCount: onInCount.Count,
            WorstRecentFmsOn: worst,
            CoverageOk: coverageOk,
            HoursSinceLastDiscomfort: hoursSinceDiscomfort,
            MeasuredDiscomfortInWindow: anyDiscomfortInWindow);
    }

    // ── Behaviour / exposure trend ─────────────────────────────────────────────────────────────────

    public sealed record ExposureEvidence(
        int BehaviourDays,
        bool TrendStableOrImproving,
        bool ExposureAtOrAboveBaseline,
        bool ExposureDeclining,
        bool BehaviorWorsening);

    /// <summary>Margin below baseline load that reads as avoidance (15%).</summary>
    private const double AvoidanceMargin = 0.15;
    /// <summary>Minimum days in EACH half before a trend is trustworthy (no thin-data "stable").</summary>
    private const int MinHalfDays = 2;

    /// <summary>Derive the exposure/outcome trend from the daily behaviour rows. OFI-p90 + session
    /// minutes are the EXPOSURE (load the user took on); spike count is the OUTCOME (symptom proxy).
    /// Splits the window into an early and a late half and compares means. Relaxation wants the late
    /// half to carry AT LEAST the early load (tolerated exposure) with spikes not rising.</summary>
    public static ExposureEvidence ExposureTrend(IReadOnlyList<DailyBehaviorAggregate>? rows)
    {
        var neutral = new ExposureEvidence(0, false, false, false, false);
        if (rows is null || rows.Count == 0) return neutral;

        var sorted = rows.Where(r => r is not null)
                         .OrderBy(r => ParseDate(r.Date))
                         .ToList();
        int days = sorted.Select(r => r.Date).Distinct().Count();
        if (sorted.Count < MinHalfDays * 2)
            return neutral with { BehaviourDays = days }; // too thin to judge a trend → not "stable"

        int half = sorted.Count / 2;
        var early = sorted.Take(half).ToList();
        var late  = sorted.Skip(sorted.Count - half).ToList();

        double earlyOfi = early.Average(r => r.OfiP90);
        double lateOfi  = late.Average(r => r.OfiP90);
        double earlyMin = early.Average(r => r.SessionMinutes);
        double lateMin  = late.Average(r => r.SessionMinutes);
        double earlySpk = early.Average(r => r.SpikeCount);
        double lateSpk  = late.Average(r => r.SpikeCount);

        // Exposure (load): at/above baseline = late load not meaningfully below early on EITHER axis.
        bool atOrAbove = lateOfi >= earlyOfi * (1 - AvoidanceMargin)
                      && lateMin >= earlyMin * (1 - AvoidanceMargin);
        // Avoidance: a meaningful DROP in either load axis (the user is dodging triggers).
        bool declining = lateOfi < earlyOfi * (1 - AvoidanceMargin)
                      || lateMin < earlyMin * (1 - AvoidanceMargin);
        // Outcome: symptoms (spikes) not rising = stable/improving; a meaningful rise = worsening.
        bool stable    = lateSpk <= earlySpk + 0.5;
        bool worsening = lateSpk > earlySpk * 1.5 + 0.5;

        return new ExposureEvidence(
            BehaviourDays: days,
            TrendStableOrImproving: stable,
            ExposureAtOrAboveBaseline: atOrAbove,
            ExposureDeclining: declining,
            BehaviorWorsening: worsening);
    }

    // ── Assemble the MeasuredState ───────────────────────────────────────────────────────────────

    /// <summary>Map the two evidence bundles + the engine/timing state into the <see cref="MeasuredState"/>
    /// the gate consumes. HoursSinceLastDiscomfort is the MINIMUM of the FMS-discomfort clock and the
    /// last manual-tighten clock (either suppresses relax). Pure.</summary>
    public static MeasuredState BuildMeasuredState(
        Cohort cohort,
        FmsEvidence fms,
        ExposureEvidence exposure,
        double baseEffectiveTrigger,
        double currentAutoDeltaPp,
        double hoursSinceLastManualTighten,
        double daysSinceLastAutoChange,
        int autoChangesThisWeek,
        double netRelaxTodayPp,
        bool hasEverAutoRelaxed)
    {
        double hoursSinceDiscomfort = Math.Min(fms.HoursSinceLastDiscomfort, hoursSinceLastManualTighten);
        return new MeasuredState(
            Cohort: cohort,
            MeasuredOnReportCount: fms.OnReportCount,
            WorstRecentFmsOn: fms.WorstRecentFmsOn,
            FmsCoverageOk: fms.CoverageOk,
            TrendStableOrImproving: exposure.TrendStableOrImproving,
            ExposureAtOrAboveBaseline: exposure.ExposureAtOrAboveBaseline,
            ExposureDeclining: exposure.ExposureDeclining,
            BehaviorWorsening: exposure.BehaviorWorsening,
            BehaviourDays: exposure.BehaviourDays,
            BaseEffectiveTrigger: baseEffectiveTrigger,
            CurrentAutoDeltaPp: currentAutoDeltaPp,
            HoursSinceLastDiscomfort: hoursSinceDiscomfort,
            DaysSinceLastAutoChange: daysSinceLastAutoChange,
            AutoChangesThisWeek: autoChangesThisWeek,
            NetRelaxTodayPp: netRelaxTodayPp,
            HasEverAutoRelaxed: hasEverAutoRelaxed,
            MeasuredDiscomfortInWindow: fms.MeasuredDiscomfortInWindow);
    }

    // ── Auto-channel timing/history (derived from the durable adjustment log) ──────────────────────

    public sealed record AutoChannelTiming(
        double DaysSinceLastAutoChange,
        int AutoChangesThisWeek,
        double NetRelaxTodayPp,
        bool HasEverAutoRelaxed,
        double HoursSinceLastManualTighten);

    /// <summary>Derive the gate's timing/history inputs from the durable adjustment log (append-ordered,
    /// <see cref="AdjustmentRecord.TimestampUtc"/>-stamped). HONORS THE RESET TARE — only records AFTER the
    /// most-recent <c>Source=="reset"</c> count, so a user Reset genuinely returns the gate to cold-start
    /// (an auto-relax cannot "survive" a Reset through the history). <c>HasEverAutoRelaxed</c> means the
    /// CUMULATIVE auto-delta crossed above baseline (a genuine net relax) — NOT merely that a positive
    /// diff was logged: the audit Delta is the per-apply DIFF, so unwinding a prior tighten (e.g. −3→0)
    /// logs a +3 diff but never goes net-positive, and must NOT trip cold-start. <paramref name="autoParameter"/>
    /// is the audit Parameter the engine writes ("AutoAdaptTrigger").</summary>
    public static AutoChannelTiming DeriveAutoChannelTiming(
        IReadOnlyList<AdjustmentRecord>? records, DateTime nowUtc, string autoParameter)
    {
        var none = new AutoChannelTiming(double.PositiveInfinity, 0, 0.0, false, double.PositiveInfinity);
        if (records is null || records.Count == 0) return none;

        // Reset tare: discard everything at/before the most-recent reset sentinel (mirrors WeightFor).
        int lastReset = -1;
        for (int i = records.Count - 1; i >= 0; i--)
            if (string.Equals(records[i]?.Source, "reset", StringComparison.Ordinal)) { lastReset = i; break; }

        var live = new List<AdjustmentRecord>();
        for (int i = lastReset + 1; i < records.Count; i++)
            if (records[i] is not null) live.Add(records[i]);

        var auto = live.Where(r => r.Parameter == autoParameter).OrderBy(r => r.TimestampUtc).ToList();

        double daysSinceLast = auto.Count > 0
            ? Math.Max(0.0, (nowUtc - auto[^1].TimestampUtc).TotalDays) : double.PositiveInfinity;
        int thisWeek = auto.Count(r => (nowUtc - r.TimestampUtc).TotalDays <= 7.0);
        // Gross same-day relax (Delta>0 only). Deliberately gross, not net: a same-day tighten does NOT
        // earn back relax budget, so the per-day cap fails toward protection (the desired direction).
        double netRelaxToday = auto.Where(r => r.TimestampUtc.Date == nowUtc.Date && r.Delta > 0).Sum(r => r.Delta);

        // Cumulative net-relax: running sum of the per-apply diffs = the auto-channel value over time;
        // "ever net-relaxed" = that sum ever exceeded baseline (0). Distinguishes a genuine relax from a
        // benign unwind of a prior tighten (the cold-start bypass the final review flagged HIGH).
        double cumulative = 0.0;
        bool everNetRelaxed = false;
        foreach (var r in auto) { cumulative += r.Delta; if (cumulative > 1e-9) { everNetRelaxed = true; break; } }

        var manualTightens = live.Where(r =>
            string.Equals(r.Source, "user", StringComparison.Ordinal)
            && (r.Parameter == "FovRadius" || r.Parameter == "LevelNudge")
            && r.Delta < 0).ToList();
        double hoursSinceManualTighten = manualTightens.Count > 0
            ? Math.Max(0.0, (nowUtc - manualTightens.Max(r => r.TimestampUtc)).TotalHours) : double.PositiveInfinity;

        return new AutoChannelTiming(daysSinceLast, thisWeek, netRelaxToday, everNetRelaxed, hoursSinceManualTighten);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────
    // (B6 Stage 2: the p75 Percentile helper was removed — the relax worst-case is now a true MAX, RT-1.)

    private static DateTime ParseDate(string? ymd)
        => DateTime.TryParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : DateTime.MaxValue; // unparseable sorts last

    private static bool TryParseUtc(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
        {
            utc = t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
            return true;
        }
        return false;
    }
}
