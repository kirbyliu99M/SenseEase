using SenseEase.Core.AdaptiveFit;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B6 Stage 3 — the gate-aware set-selector coupling. The brake that keeps the selector (band axis) and the
/// B5 auto-adaptation gate (trigger axis) as ONE coordinated loop: a candidate Band move OPPOSING the gate's
/// standing direction is penalised (W7GateConflict), exactly like a manual nudge — and it is exactly ZERO
/// when there is no standing auto-delta, so the default (consent-off) user is byte-identical to today.
/// </summary>
public sealed class B6Stage3GateCouplingTests
{
    private static readonly GridCoord Mid  = GridCoord.Set0;                      // Band 5, Shape 2 (median)
    private static readonly GridCoord Up   = new(GridCoord.Set0.Band + 1, GridCoord.Set0.Shape);
    private static readonly GridCoord Down = new(GridCoord.Set0.Band - 1, GridCoord.Set0.Shape);

    // ── GateConflict truth table (mirrors ManualConflict) ───────────────────────────────────────────

    [Theory]
    // gate TIGHTENED (negative delta ⇒ +1, prefers a HIGHER band): a DOWN move conflicts, an UP move doesn't.
    [InlineData(+1, /*down*/ true,  1)]
    [InlineData(+1, /*up*/   false, 0)]
    // gate RELAXED (positive delta ⇒ −1, prefers a LOWER band): an UP move conflicts, a DOWN move doesn't.
    [InlineData(-1, /*down*/ true,  0)]
    [InlineData(-1, /*up*/   false, 1)]
    // no standing auto-delta ⇒ NEVER a conflict (the default user).
    [InlineData(0,  /*down*/ true,  0)]
    [InlineData(0,  /*up*/   false, 0)]
    public void GateConflict_PenalisesOnlyAnOpposingBandMove(int gateDir, bool down, int expected)
        => Assert.Equal(expected, DeterministicSetSelector.GateConflict(Mid, down ? Down : Up, gateDir));

    [Fact]
    public void GateConflict_AShapeOnlyMove_IsNeverAConflict()  // Band unchanged ⇒ the gate has no say
        => Assert.Equal(0, DeterministicSetSelector.GateConflict(Mid, new GridCoord(Mid.Band, Mid.Shape + 1), +1));

    // ── Score: the penalty is applied to an opposing move, and is exactly 0 when aligned or no auto-delta ─

    [Fact]
    public void Score_AppliesGatePenalty_ToOpposingMove_ZeroWhenAlignedOrNoAutoDelta()
    {
        var w = SelectorWeights.Default;
        var stats = new UserIntensityStats(
            PressureP50: 0.5, PressureP90: 1.0, SpikeRecurrencePerHour: 0.0,
            CalmRatio: 0.5, FmsAggregate: 0.0, FmsSampleCount: 0, SampleCount: 100);

        // Baseline: the DOWN candidate's score with NO standing auto-delta (gate term contributes nothing).
        double baseDown = DeterministicSetSelector.Score(Mid, Down, stats, w, recentManualLevelDelta: 0, gateLevelDirection: 0);

        // Gate TIGHTENED (+1) ⇒ a DOWN move OPPOSES it ⇒ penalised by EXACTLY W7GateConflict (one conflict unit).
        double tightenedDown = DeterministicSetSelector.Score(Mid, Down, stats, w, 0, gateLevelDirection: +1);
        Assert.Equal(baseDown - w.W7GateConflict, tightenedDown, 9);

        // Gate RELAXED (−1) ⇒ a DOWN move is ALIGNED with it ⇒ no penalty (identical to baseline).
        double relaxedDown = DeterministicSetSelector.Score(Mid, Down, stats, w, 0, gateLevelDirection: -1);
        Assert.Equal(baseDown, relaxedDown, 9);
    }

    [Fact]
    public void Select_NoStandingAutoDelta_IsByteIdenticalToTheNoArgCall()
    {
        // The default-user no-regression: gateLevelDirection==0 (the default) must not change any decision.
        var w = SelectorWeights.Default;
        var stats = new UserIntensityStats(
            PressureP50: 0.5, PressureP90: 1.0, SpikeRecurrencePerHour: 0.0,
            CalmRatio: 0.5, FmsAggregate: 0.0, FmsSampleCount: 0, SampleCount: 100);

        var noArg = DeterministicSetSelector.Select(Mid, stats, w, recentManualLevelDelta: 0);
        var zeroGate = DeterministicSetSelector.Select(Mid, stats, w, recentManualLevelDelta: 0, gateLevelDirection: 0);
        Assert.Equal(noArg, zeroGate);
    }
}
