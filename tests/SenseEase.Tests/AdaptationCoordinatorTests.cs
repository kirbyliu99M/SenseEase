using SenseEase.Core.Adaptation;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B5 Stage 4+5 — the orchestration spine + the LLM direction-hint derivation. The coordinator is the
/// single entry point that moves the auto-channel, and it is hard-gated on consent: with consent off the
/// gate never runs and no callback fires. The hint derivation pins that the LLM may only influence
/// DIRECTION (a higher proposed sensitivity = more protection = tighten; lower = relax).
/// </summary>
public class AdaptationCoordinatorTests
{
    private static MeasuredState RelaxReady() => new(
        Cohort: Cohort.Typical, MeasuredOnReportCount: 10, WorstRecentFmsOn: 3.0, FmsCoverageOk: true,
        TrendStableOrImproving: true, ExposureAtOrAboveBaseline: true, ExposureDeclining: false,
        BehaviorWorsening: false, BehaviourDays: 20, BaseEffectiveTrigger: 85.0, CurrentAutoDeltaPp: 0.0,
        HoursSinceLastDiscomfort: 1000.0, DaysSinceLastAutoChange: 30.0, AutoChangesThisWeek: 0,
        NetRelaxTodayPp: 0.0, HasEverAutoRelaxed: true);

    private static AdaptationHint Relax(string r = "") => new(AdaptDirection.Relax, r);
    private static AdaptationHint Hold(string r = "") => new(AdaptDirection.Hold, r);

    // ── Coordinator ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConsentOff_NeverRunsGate_NoCallbacks()
    {
        bool applied = false, noticed = false;
        var coord = new AdaptationCoordinator();
        var d = coord.RunOnce(false, Relax(), RelaxReady(), _ => applied = true, (_, _) => noticed = true);
        Assert.Equal("consent-off", Assert.IsType<GateDecision.Hold>(d).Reason);
        Assert.False(applied);
        Assert.False(noticed);
    }

    [Fact]
    public void ConsentOn_GateApplies_InvokesApplyAndNotice()
    {
        double? appliedDelta = null;
        GateDecision.Apply? noticedApply = null;
        var coord = new AdaptationCoordinator();
        var d = coord.RunOnce(true, Relax("recent check-ins comfortable"), RelaxReady(),
            delta => appliedDelta = delta, (a, _) => noticedApply = a);

        var apply = Assert.IsType<GateDecision.Apply>(d);
        Assert.Equal(apply.NewAutoDeltaPp, appliedDelta);
        Assert.NotNull(noticedApply);
        Assert.True(apply.NewAutoDeltaPp > 0); // a relax (less protection)
    }

    [Fact]
    public void ConsentOn_GateHolds_NoCallbacks()
    {
        bool applied = false, noticed = false;
        var coord = new AdaptationCoordinator();
        var d = coord.RunOnce(true, Hold(), RelaxReady(), _ => applied = true, (_, _) => noticed = true);
        Assert.IsType<GateDecision.Hold>(d);
        Assert.False(applied);
        Assert.False(noticed);
    }

    [Fact]
    public void OnApplied_ReceivesRationale_RenderOnly()
    {
        string? seen = null;
        var coord = new AdaptationCoordinator();
        coord.RunOnce(true, Relax("your recent reports have been comfortable"), RelaxReady(),
            _ => { }, (_, rationale) => seen = rationale);
        Assert.Equal("your recent reports have been comfortable", seen);
    }

    // ── Hint derivation (LLM owns direction only) ─────────────────────────────────────────────────

    [Fact]
    public void Direction_HigherScale_IsTighten()       // higher scale = lower trigger = more protection
        => Assert.Equal(AdaptDirection.Tighten, AdaptationGate.DirectionFromSensitivityProposal(1.2, 1.0));

    [Fact]
    public void Direction_LowerScale_IsRelax()
        => Assert.Equal(AdaptDirection.Relax, AdaptationGate.DirectionFromSensitivityProposal(0.8, 1.0));

    [Fact]
    public void Direction_EqualScale_IsHold()
        => Assert.Equal(AdaptDirection.Hold, AdaptationGate.DirectionFromSensitivityProposal(1.0, 1.0));

    [Fact]
    public void Direction_NonFinite_IsHold()
        => Assert.Equal(AdaptDirection.Hold, AdaptationGate.DirectionFromSensitivityProposal(double.NaN, 1.0));
}
