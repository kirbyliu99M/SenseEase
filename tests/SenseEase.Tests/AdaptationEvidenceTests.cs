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

        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 7.0, 6.0);
        Assert.Equal(6, fms.OnReportCount);   // estimates never counted
    }

    [Fact]
    public void MeasuredOnFms_WorstCaseUsesP75_NotMean()
    {
        // Nine 2s + one 18: mean ≈ 3.6 (looks fine), but p75 catches the bad tail.
        var events = Enumerable.Range(0, 9).Select(i => Measured(2, SymptomEvent.InterventionStates.On, i % 3)).ToList();
        events.Add(Measured(18, SymptomEvent.InterventionStates.On, 0));
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 7.0, 6.0);
        Assert.True(fms.WorstRecentFmsOn >= 2.0); // p75 of the sorted set — not the diluted mean
        Assert.True(fms.HoursSinceLastDiscomfort < 24.0); // the 18 (>=6) set the discomfort clock today
    }

    [Fact]
    public void MeasuredOnFms_CoverageRequiresTwoDistinctDays()
    {
        var sameDay = Enumerable.Range(0, 6).Select(_ => Measured(3, SymptomEvent.InterventionStates.On, 0)).ToList();
        Assert.False(AdaptationEvidence.MeasuredOnFms(sameDay, Now, 7.0, 6.0).CoverageOk); // single-session burst

        var spread = new List<SymptomEvent> { Measured(3, SymptomEvent.InterventionStates.On, 0), Measured(3, SymptomEvent.InterventionStates.On, 2) };
        Assert.True(AdaptationEvidence.MeasuredOnFms(spread, Now, 7.0, 6.0).CoverageOk);
    }

    [Fact]
    public void MeasuredOnFms_OffStateExcludedFromOnCount_ButDiscomfortClockSeesAny()
    {
        var events = new List<SymptomEvent>
        {
            Measured(3, SymptomEvent.InterventionStates.On, 1),
            Measured(15, SymptomEvent.InterventionStates.Off, 0), // OFF + high
        };
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 7.0, 6.0);
        Assert.Equal(1, fms.OnReportCount);                  // OFF not counted in the ON aggregate
        Assert.True(fms.HoursSinceLastDiscomfort < 24.0);    // but the OFF 15 still set the discomfort clock
    }

    [Fact]
    public void MeasuredOnFms_NoData_IsNone()
    {
        var fms = AdaptationEvidence.MeasuredOnFms(new List<SymptomEvent>(), Now, 7.0, 6.0);
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
        var fms = new AdaptationEvidence.FmsEvidence(8, 3.0, true, 100.0);    // last FMS discomfort 100h ago
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
        for (int i = 0; i < 9; i++) events.Add(Measured(3, SymptomEvent.InterventionStates.On, i % 3)); // calm, 3 days
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 40, 30, 1), Day("2026-06-11", 40, 30, 1), Day("2026-06-12", 40, 30, 1),
            Day("2026-06-13", 42, 32, 1), Day("2026-06-14", 42, 32, 1), Day("2026-06-15", 42, 32, 1),
        };
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 7.0, 6.0);
        var exp = AdaptationEvidence.ExposureTrend(rows);
        var s = AdaptationEvidence.BuildMeasuredState(Cohort.Typical, fms, exp, 85, 0,
            double.PositiveInfinity, 30, 0, 0, hasEverAutoRelaxed: true);

        var d = AdaptationGate.Evaluate(new AdaptationHint(AdaptDirection.Relax, ""), s, AdaptationGateConfig.Default);
        Assert.IsType<GateDecision.Apply>(d);
    }

    [Fact]
    public void EndToEnd_Avoidance_GateHolds()
    {
        var events = new List<SymptomEvent>();
        for (int i = 0; i < 9; i++) events.Add(Measured(3, SymptomEvent.InterventionStates.On, i % 3));
        var rows = new List<DailyBehaviorAggregate>
        {
            Day("2026-06-10", 50, 40, 1), Day("2026-06-11", 50, 40, 1), Day("2026-06-12", 50, 40, 1),
            Day("2026-06-13", 20, 15, 1), Day("2026-06-14", 20, 15, 1), Day("2026-06-15", 20, 15, 1), // load collapsed
        };
        var fms = AdaptationEvidence.MeasuredOnFms(events, Now, 7.0, 6.0);
        var exp = AdaptationEvidence.ExposureTrend(rows);
        var s = AdaptationEvidence.BuildMeasuredState(Cohort.Typical, fms, exp, 85, 0,
            double.PositiveInfinity, 30, 0, 0, hasEverAutoRelaxed: true);

        var d = Assert.IsType<GateDecision.Hold>(
            AdaptationGate.Evaluate(new AdaptationHint(AdaptDirection.Relax, ""), s, AdaptationGateConfig.Default));
        Assert.Equal("insufficient-tolerated-exposure", d.Reason);
    }
}
