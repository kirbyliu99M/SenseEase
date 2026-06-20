using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Adaptation;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B5 Stage 3 — the pure assembly of MeasuredState the gate trusts. Pins the two load-bearing
/// guarantees: (1) the FMS corroboration excludes chat-derived nl_daily estimates (un-injectable);
/// (2) relaxation evidence is positive tolerated-exposure, so avoidance (declining load) reads as a
/// block, not as "improving". Ends with end-to-end checks: assembled evidence → gate decision.
/// </summary>
public class AdaptationEvidenceTests
{
    private static readonly DateTime Now = new(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);

    private static SymptomEvent Measured(int value, string state, double daysAgo, string kind = SymptomEvent.Kinds.FmsReport)
        => new()
        {
            Measured = kind == SymptomEvent.Kinds.FmsReport,
            Kind = kind,
            Value = value,
            InterventionState = state,
            Source = SymptomEvent.Sources.Slider,
            TsUtc = Now.AddDays(-daysAgo).ToString("o"),
        };

    private static SymptomEvent NlDaily(int value, double daysAgo)
        => new()
        {
            Measured = false,
            Kind = SymptomEvent.Kinds.FmsEstimate,
            Source = SymptomEvent.Sources.NlDaily,
            Value = value,
            TsUtc = Now.AddDays(-daysAgo).ToString("o"),
        };

    private static DailyBehaviorAggregate Day(string date, int ofiP90, int sessionMin, int spikes)
        => new() { Date = date, OfiP90 = ofiP90, SessionMinutes = sessionMin, SpikeCount = spikes };

    // ── MeasuredOnFms ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MeasuredOnFms_ExcludesNlDailyEstimates_UnInjectable()
    {
        var events = new List<SymptomEvent>();
        for (int i = 0; i < 6; i++) events.Add(Measured(3, SymptomEvent.InterventionStates.On, i % 3)); // 6 measured, 3 days
        for (int i = 0; i < 10; i++) events.Add(NlDaily(2, i % 3));                                       // 10 chat-derived

        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 28.0, 10.0, 6.0);
        Assert.Equal(6, fms.OnReportCount);   // estimates never counted
    }

    [Fact]
    public void MeasuredOnFms_WorstCaseUsesMax_NotADilutablePercentile()
    {
        // RT-1: nine calm 2s + one bad 18 in the recency window. A p75 would dilute the 18 out of the tail
        // (≈ 2) and wrongly permit relax; a MAX cannot be diluted — the single bad report stands.
        var events = Enumerable.Range(0, 9).Select(i => Measured(2, SymptomEvent.InterventionStates.On, i % 3)).ToList();
        events.Add(Measured(18, SymptomEvent.InterventionStates.On, 0));
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 28.0, 10.0, 6.0);
        Assert.Equal(18.0, fms.WorstRecentFmsOn, 6);       // MAX, undiluted (a p75 would be ~2)
        Assert.True(fms.MeasuredDiscomfortInWindow);        // the 18 (>=6) also flags discomfort-in-window
        Assert.True(fms.HoursSinceLastDiscomfort < 24.0);   // … and set the discomfort clock today
    }

    [Fact]
    public void MeasuredOnFms_Coverage_IsWindowProportional()
    {
        // RT-2c: over a 28-day count window, coverage needs ceil(28/7)=4 distinct days, no single day >50% of
        // the count, and ≥1 ON-report in the last 7 days — a short calm island in a long window is rejected.
        var sameDay = Enumerable.Range(0, 6).Select(_ => Measured(3, SymptomEvent.InterventionStates.On, 0)).ToList();
        Assert.False(AdaptationEvidence.MeasuredOnFms(sameDay, Now, 28.0, 10.0, 6.0).CoverageOk); // 1 day

        var twoDayIsland = new List<SymptomEvent>
        {
            Measured(3, SymptomEvent.InterventionStates.On, 0), Measured(3, SymptomEvent.InterventionStates.On, 1),
        };
        Assert.False(AdaptationEvidence.MeasuredOnFms(twoDayIsland, Now, 28.0, 10.0, 6.0).CoverageOk); // only 2 of 4 days

        // Spread across 4 distinct days (one each), with a report in the last 7 days → coverage OK.
        var spread = new List<SymptomEvent>
        {
            Measured(3, SymptomEvent.InterventionStates.On, 0), Measured(3, SymptomEvent.InterventionStates.On, 2),
            Measured(3, SymptomEvent.InterventionStates.On, 4), Measured(3, SymptomEvent.InterventionStates.On, 6),
        };
        Assert.True(AdaptationEvidence.MeasuredOnFms(spread, Now, 28.0, 10.0, 6.0).CoverageOk);
    }

    [Fact]
    public void MeasuredOnFms_OffStateExcludedFromOnCount_ButDiscomfortClockSeesAny()
    {
        var events = new List<SymptomEvent>
        {
            Measured(3, SymptomEvent.InterventionStates.On, 1),
            Measured(15, SymptomEvent.InterventionStates.Off, 0), // OFF + high
        };
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 28.0, 10.0, 6.0);
        Assert.Equal(1, fms.OnReportCount);                  // OFF not counted in the ON aggregate
        Assert.True(fms.HoursSinceLastDiscomfort < 24.0);    // but the OFF 15 still set the discomfort clock
    }

    [Fact]
    public void MeasuredOnFms_NoData_IsNone()
    {
        var fms = AdaptationEvidence.MeasuredOnFms(new List<SymptomEvent>(), Now, 28.0, 10.0, 6.0);
        Assert.Equal(0, fms.OnReportCount);
        Assert.True(double.IsPositiveInfinity(fms.HoursSinceLastDiscomfort));
    }

    // ── ExposureTrend ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExposureTrend_ToleratedExposure_StableSpikes_IsRelaxable()
    {
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 40, 30, 2), Day("2026-06-11", 40, 30, 2), Day("2026-06-12", 40, 30, 2),
            Day("2026-06-13", 42, 32, 1), Day("2026-06-14", 42, 32, 1), Day("2026-06-15", 42, 32, 1),
        };
        var t = AdaptationEvidence.ExposureTrend(rows);
        Assert.True(t.ExposureAtOrAboveBaseline);
        Assert.False(t.ExposureDeclining);
        Assert.True(t.TrendStableOrImproving);
        Assert.False(t.BehaviorWorsening);
        Assert.Equal(6, t.BehaviourDays);
    }

    [Fact]
    public void ExposureTrend_DecliningLoad_ReadsAsAvoidance()
    {
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 50, 40, 1), Day("2026-06-11", 50, 40, 1), Day("2026-06-12", 50, 40, 1),
            Day("2026-06-13", 20, 15, 1), Day("2026-06-14", 20, 15, 1), Day("2026-06-15", 20, 15, 1),
        };
        var t = AdaptationEvidence.ExposureTrend(rows);
        Assert.True(t.ExposureDeclining);            // load dropped → avoidance
        Assert.False(t.ExposureAtOrAboveBaseline);
    }

    [Fact]
    public void ExposureTrend_RisingSpikes_IsWorsening()
    {
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 40, 30, 1), Day("2026-06-11", 40, 30, 1), Day("2026-06-12", 40, 30, 1),
            Day("2026-06-13", 40, 30, 4), Day("2026-06-14", 40, 30, 4), Day("2026-06-15", 40, 30, 4),
        };
        var t = AdaptationEvidence.ExposureTrend(rows);
        Assert.True(t.BehaviorWorsening);
        Assert.False(t.TrendStableOrImproving);
    }

    [Fact]
    public void ExposureTrend_TooFewDays_IsNotStable()
    {
        var rows = new List<DailyBehaviorAggregate> { Day("2026-06-14", 40, 30, 1), Day("2026-06-15", 40, 30, 1) };
        var t = AdaptationEvidence.ExposureTrend(rows);
        Assert.False(t.TrendStableOrImproving);  // thin data never declares "stable"
        Assert.Equal(2, t.BehaviourDays);
    }

    // ── BuildMeasuredState ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_HoursSinceDiscomfort_IsMinOfFmsAndManualTighten()
    {
        var fms = new AdaptationEvidence.FmsEvidence(8, 3.0, true, 100.0, false); // last FMS discomfort 100h ago; none in-window
        var exp = new AdaptationEvidence.ExposureEvidence(10, true, true, false, false);
        var s = AdaptationEvidence.BuildMeasuredState(Cohort.Typical, fms, exp,
            baseEffectiveTrigger: 85, currentAutoDeltaPp: 0,
            hoursSinceLastManualTighten: 10.0,  // a manual tighten 10h ago is more recent
            daysSinceLastAutoChange: 30, autoChangesThisWeek: 0, netRelaxTodayPp: 0, hasEverAutoRelaxed: true);
        Assert.Equal(10.0, s.HoursSinceLastDiscomfort, 6);
    }

    // ── End-to-end: assembled evidence → gate decision ────────────────────────────────────────────

    [Fact]
    public void EndToEnd_GoodEvidence_GateRelaxes()
    {
        var events = new List<SymptomEvent>();
        for (int i = 0; i < 9; i++) events.Add(Measured(3, SymptomEvent.InterventionStates.On, i)); // calm, 9 distinct days // calm, 3 days
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 40, 30, 1), Day("2026-06-11", 40, 30, 1), Day("2026-06-12", 40, 30, 1),
            Day("2026-06-13", 42, 32, 1), Day("2026-06-14", 42, 32, 1), Day("2026-06-15", 42, 32, 1),
        };
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 28.0, 10.0, 6.0);
        var exp = AdaptationEvidence.ExposureTrend(rows);
        var s = AdaptationEvidence.BuildMeasuredState(Cohort.Typical, fms, exp, 85, 0,
            double.PositiveInfinity, 30, 0, 0, hasEverAutoRelaxed: true);

        var d = AdaptationGate.Evaluate(new AdaptationHint(AdaptDirection.Relax, ""), s, AdaptationGateConfig.Default);
        Assert.IsType<GateDecision.Apply>(d);
    }

    // ── DeriveAutoChannelTiming (the wired history derivation — final-review HIGH fixes) ──────────

    private const string AutoParam = "AutoAdaptTrigger";

    private static AdjustmentRecord Rec(string param, double delta, string source, double daysAgo)
        => new() { Parameter = param, Delta = delta, Source = source, TimestampUtc = Now.AddDays(-daysAgo) };

    [Fact]
    public void Timing_HasEverAutoRelaxed_FalseForUnwindOfPriorTighten()
    {
        // tighten (−3) then unwind back to baseline (+3): cumulative goes −3 → 0, never net-positive.
        // Must NOT count as "ever relaxed" (else the first genuine relax bypasses cold-start — HIGH).
        var t = AdaptationEvidence.DeriveAutoChannelTiming(
            new List<AdjustmentRecord> { Rec(AutoParam, -3, "system", 5), Rec(AutoParam, +3, "system", 1) }, Now, AutoParam);
        Assert.False(t.HasEverAutoRelaxed);
    }

    [Fact]
    public void Timing_HasEverAutoRelaxed_TrueForGenuineNetRelax()
    {
        var t = AdaptationEvidence.DeriveAutoChannelTiming(
            new List<AdjustmentRecord> { Rec(AutoParam, +3, "system", 1) }, Now, AutoParam);
        Assert.True(t.HasEverAutoRelaxed);
    }

    [Fact]
    public void Timing_HasEverAutoRelaxed_TrueOnceCumulativeCrossesBaseline()
    {
        // −3, +3, +3 ⇒ cumulative −3, 0, +3 ⇒ crosses baseline on the last ⇒ true.
        var t = AdaptationEvidence.DeriveAutoChannelTiming(
            new List<AdjustmentRecord> { Rec(AutoParam, -3, "system", 5), Rec(AutoParam, +3, "system", 3), Rec(AutoParam, +3, "system", 1) },
            Now, AutoParam);
        Assert.True(t.HasEverAutoRelaxed);
    }

    [Fact]
    public void Timing_ResetTare_ClearsPreResetHistory()
    {
        // A relax then a Reset ⇒ the gate must see cold-start again (no surviving auto history).
        var t = AdaptationEvidence.DeriveAutoChannelTiming(
            new List<AdjustmentRecord> { Rec(AutoParam, +3, "system", 5), Rec("Reset", 0, "reset", 3) }, Now, AutoParam);
        Assert.False(t.HasEverAutoRelaxed);
        Assert.True(double.IsPositiveInfinity(t.DaysSinceLastAutoChange));
        Assert.Equal(0, t.AutoChangesThisWeek);
    }

    [Fact]
    public void Timing_ResetTare_KeepsPostResetHistory()
    {
        var t = AdaptationEvidence.DeriveAutoChannelTiming(
            new List<AdjustmentRecord> { Rec(AutoParam, +3, "system", 5), Rec("Reset", 0, "reset", 3), Rec(AutoParam, +3, "system", 1) },
            Now, AutoParam);
        Assert.True(t.HasEverAutoRelaxed);   // the post-reset relax
        Assert.Equal(1, t.AutoChangesThisWeek);
    }

    [Fact]
    public void Timing_Counts_DaysAndWeekAndNetRelaxAndManualTighten()
    {
        var t = AdaptationEvidence.DeriveAutoChannelTiming(new List<AdjustmentRecord>
        {
            Rec(AutoParam, +3, "system", 0),   // a relax today
            Rec(AutoParam, -3, "system", 0),   // a tighten today (not added to gross relax)
            Rec("LevelNudge", -8, "user", 1),  // a manual tighten ~24h ago
        }, Now, AutoParam);
        Assert.Equal(2, t.AutoChangesThisWeek);
        Assert.Equal(3.0, t.NetRelaxTodayPp, 9);                 // gross same-day relax only
        Assert.True(t.HoursSinceLastManualTighten is >= 23.0 and <= 25.0);
        Assert.True(t.DaysSinceLastAutoChange < 0.01);            // most recent auto change is today
    }

    [Fact]
    public void Timing_NoRecords_IsNeutral()
    {
        var t = AdaptationEvidence.DeriveAutoChannelTiming(new List<AdjustmentRecord>(), Now, AutoParam);
        Assert.False(t.HasEverAutoRelaxed);
        Assert.True(double.IsPositiveInfinity(t.DaysSinceLastAutoChange));
        Assert.True(double.IsPositiveInfinity(t.HoursSinceLastManualTighten));
    }

    [Fact]
    public void EndToEnd_Avoidance_GateHolds()
    {
        var events = new List<SymptomEvent>();
        for (int i = 0; i < 9; i++) events.Add(Measured(3, SymptomEvent.InterventionStates.On, i)); // calm, 9 distinct days
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 50, 40, 1), Day("2026-06-11", 50, 40, 1), Day("2026-06-12", 50, 40, 1),
            Day("2026-06-13", 20, 15, 1), Day("2026-06-14", 20, 15, 1), Day("2026-06-15", 20, 15, 1), // load collapsed
        };
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 28.0, 10.0, 6.0);
        var exp = AdaptationEvidence.ExposureTrend(rows);
        var s = AdaptationEvidence.BuildMeasuredState(Cohort.Typical, fms, exp, 85, 0,
            double.PositiveInfinity, 30, 0, 0, hasEverAutoRelaxed: true);

        var d = Assert.IsType<GateDecision.Hold>(
            AdaptationGate.Evaluate(new AdaptationHint(AdaptDirection.Relax, ""), s, AdaptationGateConfig.Default));
        Assert.Equal("insufficient-tolerated-exposure", d.Reason);
    }
}
