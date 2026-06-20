using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Adaptation;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B6 Stage 2 — the evidence-reachability re-tune. The POINT: a normally-engaged user (~2 measured ON-FMS
/// per week, sustained comfort) can now actually CLEAR the relax evidence bar over a 28-day COUNT window,
/// while every safety floor is held or tightened. Pins the reachability PAIR (reaches relax under the new
/// windows, rejected under the old 7-day one) + the RT-1 (MAX-not-p75) and RT-3d (any-discomfort-in-count-
/// window) guards end-to-end (assembled evidence → gate decision). Pure; no engine/LLM.
/// </summary>
public sealed class B6Stage2ReachabilityTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
    private const double Count = 28.0, Recency = 10.0, Threshold = 6.0;

    private static SymptomEvent OnFms(int value, double daysAgo)
        => new()
        {
            Measured = true, Kind = SymptomEvent.Kinds.FmsReport, Value = value,
            InterventionState = SymptomEvent.InterventionStates.On, Source = SymptomEvent.Sources.Slider,
            TsUtc = Now.AddDays(-daysAgo).ToString("o"),
        };

    /// <summary>~2 measured ON-reports/week over 4 weeks = 8 reports, all calm (value 3).</summary>
    private static List<SymptomEvent> EngagedCalmUser()
        => new double[] { 0, 3, 7, 10, 14, 17, 21, 24 }.Select(d => OnFms(3, d)).ToList();

    /// <summary>16 stable behaviour days at a steady tolerated load (cold-start needs ≥ 14).</summary>
    private static List<DailyBehaviorAggregate> StableBehaviour(int days = 16)
        => Enumerable.Range(0, days)
            .Select(i => new DailyBehaviorAggregate
            {
                Date = Now.AddDays(-(i + 1)).ToString("yyyy-MM-dd"),
                OfiP90 = 40, SessionMinutes = 30, SpikeCount = 1,
            }).ToList();

    private static MeasuredState State(IEnumerable<SymptomEvent> fmsEvents, double countWin, double recencyWin,
        IReadOnlyList<DailyBehaviorAggregate> behaviour, bool everRelaxed = false)
    {
        var fms = AdaptationEvidence.MeasuredOnFms(fmsEvents, Now, countWin, recencyWin, Threshold);
        var exp = AdaptationEvidence.ExposureTrend(behaviour);
        return AdaptationEvidence.BuildMeasuredState(Cohort.Typical, fms, exp,
            baseEffectiveTrigger: 85, currentAutoDeltaPp: 0, hoursSinceLastManualTighten: double.PositiveInfinity,
            daysSinceLastAutoChange: 30, autoChangesThisWeek: 0, netRelaxTodayPp: 0, hasEverAutoRelaxed: everRelaxed);
    }

    private static GateDecision RelaxEval(MeasuredState s)
        => AdaptationGate.Evaluate(new AdaptationHint(AdaptDirection.Relax, ""), s, AdaptationGateConfig.Default);

    // ── The reachability PAIR (the whole point of Stage 2) ──────────────────────────────────────────

    [Fact]
    public void EngagedCalmUser_ReachesRelax_UnderNewWindows_ButRejected_UnderOld7d()
    {
        var behaviour = StableBehaviour();

        // NEW: the 28-day count window admits all 8 reports + cold-start (≥8 reports, ≥14 behaviour days)
        // clears, with worst-case (MAX over recency) and discomfort both clean → the gate RELAXES.
        var apply = Assert.IsType<GateDecision.Apply>(RelaxEval(State(EngagedCalmUser(), Count, Recency, behaviour)));
        Assert.Equal(AdaptDirection.Relax, apply.Direction);

        // OLD 7-day window (simulated count=recency=7): only the last-7d reports count → < 8 → cold-start
        // rejects. Same engaged user, same comfort — unreachable under the old horizon. This is the unlock.
        var hold = Assert.IsType<GateDecision.Hold>(RelaxEval(State(EngagedCalmUser(), 7.0, 7.0, behaviour)));
        Assert.Equal("cold-start-fms-reports", hold.Reason);
    }

    // ── RT-1: a bad report in the recency window blocks relax, undiluted by the calm ones ───────────

    [Fact]
    public void OneBadReport_InRecency_BlocksRelax_NotDiluted()
    {
        var events = EngagedCalmUser();
        events.Add(OnFms(7, 0)); // one fresh 7 among eight calm 2-3s — a p75 would dilute it; MAX cannot.
        var hold = Assert.IsType<GateDecision.Hold>(RelaxEval(State(events, Count, Recency, StableBehaviour())));
        Assert.Contains(hold.Reason, new[] { "discomfort-signal-relax-suppressed", "recent-fms-too-high" });
    }

    // ── RT-3d: a bad report OUTSIDE the 10d recency but INSIDE the 28d count window still blocks ─────

    [Fact]
    public void OneBadReport_OutsideRecency_ButInCountWindow_StillBlocks_ViaDiscomfortInWindow()
    {
        var events = EngagedCalmUser();
        events.Add(OnFms(8, 15)); // 15 days ago: the recency-MAX (10d) misses it, but it padded the count …
        var s = State(events, Count, Recency, StableBehaviour());
        Assert.True(s.MeasuredDiscomfortInWindow); // … so the any-discomfort-in-count-window flag catches it
        var hold = Assert.IsType<GateDecision.Hold>(RelaxEval(s));
        Assert.Equal("discomfort-signal-relax-suppressed", hold.Reason);
    }
}
