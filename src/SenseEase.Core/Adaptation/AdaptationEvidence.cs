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
        double HoursSinceLastDiscomfort);

    /// <summary>Aggregate the MEASURED, protection-ON FMS reports in the trailing window. Excludes
    /// <c>nl_daily</c>/estimate rows (the un-injectable invariant). Worst-case = window p75 (a single
    /// fresh bad episode keeps it high). Coverage = reports span ≥ 2 distinct days (not a single-session
    /// burst). HoursSinceLastDiscomfort = hours since the most recent MEASURED report (any intervention
    /// state) at/above <paramref name="discomfortThreshold"/>, else +∞.</summary>
    public static FmsEvidence MeasuredOnFms(
        IEnumerable<SymptomEvent>? events, DateTime nowUtc, double windowDays, double discomfortThreshold)
    {
        var none = new FmsEvidence(0, 0.0, false, double.PositiveInfinity);
        if (events is null) return none;

        DateTime since = nowUtc.AddDays(-windowDays);
        var inWindow = new List<(DateTime ts, SymptomEvent e)>();
        double hoursSinceDiscomfort = double.PositiveInfinity;

        foreach (var e in events)
        {
            if (e is null || !e.Measured || e.Kind != SymptomEvent.Kinds.FmsReport) continue; // MEASURED-ONLY
            if (!TryParseUtc(e.TsUtc, out DateTime ts) || ts < since) continue;
            inWindow.Add((ts, e));

            // Discomfort clock: any measured report at/above threshold, regardless of ON/OFF.
            if (e.Value >= discomfortThreshold)
            {
                double hrs = (nowUtc - ts).TotalHours;
                if (hrs >= 0 && hrs < hoursSinceDiscomfort) hoursSinceDiscomfort = hrs;
            }
        }

        var on = inWindow
            .Where(x => x.e.InterventionState == SymptomEvent.InterventionStates.On)
            .ToList();
        if (on.Count == 0) return none with { HoursSinceLastDiscomfort = hoursSinceDiscomfort };

        double[] values = on.Select(x => (double)x.e.Value).OrderBy(v => v).ToArray();
        double p75 = Percentile(values, 0.75);
        int distinctDays = on.Select(x => x.ts.Date).Distinct().Count();

        return new FmsEvidence(
            OnReportCount: on.Count,
            WorstRecentFmsOn: p75,
            CoverageOk: distinctDays >= 2,
            HoursSinceLastDiscomfort: hoursSinceDiscomfort);
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
            HasEverAutoRelaxed: hasEverAutoRelaxed);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Linear-interpolated percentile over an ascending array (PERCENTILE.INC); single sample
    /// returns itself; q clamped to [0,1].</summary>
    private static double Percentile(double[] sortedAscending, double q)
    {
        int n = sortedAscending.Length;
        if (n == 0) return 0.0;
        if (n == 1) return sortedAscending[0];
        if (q <= 0.0) return sortedAscending[0];
        if (q >= 1.0) return sortedAscending[n - 1];
        double rank = q * (n - 1);
        int lo = (int)Math.Floor(rank);
        int hi = lo + 1;
        double frac = rank - lo;
        if (hi >= n) return sortedAscending[n - 1];
        return sortedAscending[lo] + frac * (sortedAscending[hi] - sortedAscending[lo]);
    }

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
