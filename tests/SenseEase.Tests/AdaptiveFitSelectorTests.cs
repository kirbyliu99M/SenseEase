using System.Linq;
using SenseEase.Core.AdaptiveFit;
using SenseEase.Core.Configuration;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 core GROUP C — the deterministic selector + the cooldown/lifecycle state machine,
/// shipped BEHIND the default-OFF <c>AppConfig.AdaptiveFitSelectorEnabled</c> flag.
///
/// <para>Two pillars, matching the locked spec:</para>
/// <list type="number">
///   <item><b>SELECTOR</b> (<see cref="DeterministicSetSelector"/>) — no-move-on-noise
///   (flat/neutral stats Hold); adjacency-only (a move target is never non-adjacent);
///   the confidence gate (argmax must beat Hold by ≥ margin); manual-conflict defers
///   (a <c>recentManualLevelDelta</c> opposing a candidate suppresses it); min-samples
///   (thin data Holds).</item>
///   <item><b>STATE MACHINE</b> (<see cref="AdaptiveFitStateMachine"/>) — the
///   Settling→Active→Cooldown→Active cycle; dual-trigger precedence (manual RESETS the
///   timer, set-change ENTERS cooldown, decline DOUBLES the remaining wait); no-oscillation
///   (alternating borderline stats don't ping-pong A→B→A inside one cooldown).</item>
/// </list>
///
/// <para>All pure Core — no STA, no engine, no I/O. Stats are this single user's own window
/// only (NOT collaborative filtering).</para>
/// </summary>
public class AdaptiveFitSelectorTests
{
    // ── Stats fixtures ─────────────────────────────────────────────────────────
    // The shared locked UserIntensityStats shape (Group B owns the type; C consumes it):
    //   (PressureP50, PressureP90, SpikeRecurrencePerHour, CalmRatio, FmsAggregate, FmsSampleCount, SampleCount).
    //   FmsSampleCount: 0 on these fixtures ⇒ the B1 FMS outcome term is inert (pressure/spike/calm only).

    private static readonly int EnoughSamples = SenseDefaults.SelectorMinSamplesBeforeAnyMove + 5;

    /// <summary>A flat, neutral window that best fits the MEDIAN band (set0): pressure ≈
    /// trigger (P90 norm ≈ 0.5), calm ≈ 0.5, no spikes. Every neighbour is a worse fit than
    /// staying, so the selector must Hold even with ample samples.</summary>
    private static UserIntensityStats Neutral(int samples = 0) => new(
        PressureP50: 1.0,             // at trigger
        PressureP90: 1.0,             // norm = 1.0 / 2.0 = 0.5  → median band target
        SpikeRecurrencePerHour: 0.0,
        CalmRatio: 0.5,
        FmsAggregate: 5.0,
        FmsSampleCount: 0,
        SampleCount: samples == 0 ? EnoughSamples : samples);

    /// <summary>A clearly high-intensity window: sustained high pressure (P90 well above
    /// trigger ⇒ norm → 1.0), low calm, frequent spikes. This should pull UP in Band
    /// (and up in Shape via spikes).</summary>
    private static UserIntensityStats HighIntensity(int samples = 0) => new(
        PressureP50: 1.6,
        PressureP90: 2.0,             // norm = 1.0 (full scale)  → top band target
        SpikeRecurrencePerHour: 6.0, // norm = 1.0 (full scale)  → top shape target
        CalmRatio: 0.05,             // barely any calm
        FmsAggregate: 16.0,
        FmsSampleCount: 0,
        SampleCount: samples == 0 ? EnoughSamples : samples);

    /// <summary>A clearly calm window: low pressure, high calm. This should pull DOWN in Band.</summary>
    private static UserIntensityStats Calm(int samples = 0) => new(
        PressureP50: 0.1,
        PressureP90: 0.2,             // norm = 0.1  → low band target
        SpikeRecurrencePerHour: 0.0,
        CalmRatio: 0.95,             // mostly calm
        FmsAggregate: 1.0,
        FmsSampleCount: 0,
        SampleCount: samples == 0 ? EnoughSamples : samples);

    // ──────────────────────────────────────────────────────────────────────────
    // 1. SELECTOR
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoMoveOnNoise_FlatNeutralStatsAtSet0_Holds()
    {
        // Neutral stats best-fit the median band, so no neighbour beats staying by the
        // margin: the selector must Hold even though there is plenty of data.
        var decision = DeterministicSetSelector.Select(
            GridCoord.Set0, Neutral(), SelectorWeights.Default, recentManualLevelDelta: 0);

        Assert.IsType<SelectorDecision.Hold>(decision);
    }

    [Fact]
    public void MinSamples_ThinData_Holds_EvenWhenStatsStronglyFavourAMove()
    {
        // High-intensity stats would normally pull UP, but with too few samples the
        // confidence gate forces Hold (thin data must never move the set).
        var thin = HighIntensity(samples: SenseDefaults.SelectorMinSamplesBeforeAnyMove - 1);

        var decision = DeterministicSetSelector.Select(
            GridCoord.Set0, thin, SelectorWeights.Default, recentManualLevelDelta: 0);

        Assert.IsType<SelectorDecision.Hold>(decision);

        // One more sample (crossing the floor) with the same strong signal DOES move — proves
        // the gate, not the signal, was the blocker.
        var atFloor = HighIntensity(samples: SenseDefaults.SelectorMinSamplesBeforeAnyMove);
        var moved = DeterministicSetSelector.Select(
            GridCoord.Set0, atFloor, SelectorWeights.Default, recentManualLevelDelta: 0);
        Assert.IsType<SelectorDecision.MoveTo>(moved);
    }

    [Fact]
    public void AdjacencyOnly_AnyMoveTargetIsAlwaysExactlyOneManhattanStepAway()
    {
        // Sweep every cell × a spread of stats; whenever a move is returned, the target must
        // be in-bounds and at Manhattan distance EXACTLY 1 from the current cell.
        UserIntensityStats[] fixtures = { Neutral(), HighIntensity(), Calm() };

        for (int band = GridCoord.MinBand; band <= GridCoord.MaxBand; band++)
        for (int shape = GridCoord.MinShape; shape <= GridCoord.MaxShape; shape++)
        {
            var current = new GridCoord(band, shape);
            foreach (var stats in fixtures)
            foreach (var manual in new[] { -1, 0, 1 })
            {
                var decision = DeterministicSetSelector.Select(
                    current, stats, SelectorWeights.Default, manual);

                if (decision is SelectorDecision.MoveTo move)
                {
                    Assert.True(move.Target.InBounds, $"target {move.Target} out of bounds");
                    Assert.Equal(1, GridCoord.ManhattanDistance(current, move.Target));
                    // And it is genuinely one of the von-Neumann neighbours.
                    Assert.Contains(move.Target, GridCoord.AdjacentCells(current).ToList());
                }
            }
        }
    }

    // ── B1: measured-FMS OUTCOME term (gated on FmsSampleCount > 0) ──────────────

    [Fact]
    public void FmsTerm_Inert_WhenNoReports()
    {
        // FmsSampleCount == 0 ⇒ the FMS term contributes nothing, whatever FmsAggregate says.
        var current = GridCoord.Set0;
        var up = GridCoord.AdjacentCells(current).First(c => c.Band > current.Band);
        var w = SelectorWeights.Default;

        var lowFms  = new UserIntensityStats(1.0, 1.0, 0.0, 0.5, FmsAggregate: 0.0,  FmsSampleCount: 0, SampleCount: 20);
        var highFms = new UserIntensityStats(1.0, 1.0, 0.0, 0.5, FmsAggregate: 18.0, FmsSampleCount: 0, SampleCount: 20);

        Assert.Equal(
            DeterministicSetSelector.Score(current, up, lowFms,  w, 0),
            DeterministicSetSelector.Score(current, up, highFms, w, 0), 9);
    }

    [Fact]
    public void FmsTerm_HighMeasuredDiscomfort_RaisesHigherBandScore()
    {
        // Identical window; the ONLY difference is that measured FMS reports are now present and
        // high. The higher-Band candidate's score must rise — the outcome signal biases upward.
        var current = GridCoord.Set0;
        var up = GridCoord.AdjacentCells(current).First(c => c.Band > current.Band);
        var w = SelectorWeights.Default;

        var without = new UserIntensityStats(1.0, 1.0, 0.0, 0.5, FmsAggregate: 18.0, FmsSampleCount: 0, SampleCount: 20);
        var with    = new UserIntensityStats(1.0, 1.0, 0.0, 0.5, FmsAggregate: 18.0, FmsSampleCount: 6, SampleCount: 20);

        Assert.True(
            DeterministicSetSelector.Score(current, up, with, w, 0) >
            DeterministicSetSelector.Score(current, up, without, w, 0),
            "high measured FMS should raise the higher-Band candidate's score");
    }

    [Fact]
    public void ConfidenceGate_ArgmaxMustBeatHoldByAtLeastTheMargin()
    {
        // From the median set, high-intensity stats clearly favour moving UP in band. With a
        // normal margin the move fires…
        var current = GridCoord.Set0;
        var stats = HighIntensity();

        var moved = DeterministicSetSelector.Select(
            current, stats, SelectorWeights.Default, recentManualLevelDelta: 0);
        Assert.IsType<SelectorDecision.MoveTo>(moved);

        // …but inflate the margin past any achievable advantage and the SAME inputs Hold —
        // proving the gate is the margin comparison, not the raw scores.
        var strict = SelectorWeights.Default with { ConfidenceMargin = 1000.0 };
        var held = DeterministicSetSelector.Select(current, stats, strict, recentManualLevelDelta: 0);
        Assert.IsType<SelectorDecision.Hold>(held);
    }

    [Fact]
    public void ConfidenceGate_MoveDirectionFollowsTheSignal_UpForHotDownForCalm()
    {
        var current = new GridCoord(5, 2);

        // Hot window pulls the BAND up.
        var up = DeterministicSetSelector.Select(
            current, HighIntensity(), SelectorWeights.Default, 0);
        var upMove = Assert.IsType<SelectorDecision.MoveTo>(up);
        Assert.True(upMove.Target.Band >= current.Band,
            $"hot stats should not move band DOWN (got {upMove.Target})");

        // Calm window pulls the BAND down.
        var down = DeterministicSetSelector.Select(
            current, Calm(), SelectorWeights.Default, 0);
        var downMove = Assert.IsType<SelectorDecision.MoveTo>(down);
        Assert.True(downMove.Target.Band <= current.Band,
            $"calm stats should not move band UP (got {downMove.Target})");
    }

    [Fact]
    public void ManualConflict_AnOpposingRecentManualDelta_SuppressesThatMove()
    {
        // Calm stats want to move the band DOWN. If the user JUST manually went UP
        // (delta > 0), the down-move opposes the user and must be suppressed (Hold or a
        // non-down move) — user authority defers.
        var current = new GridCoord(5, 2);

        // Baseline: no manual input → calm stats move DOWN.
        var baseline = DeterministicSetSelector.Select(current, Calm(), SelectorWeights.Default, 0);
        var baseMove = Assert.IsType<SelectorDecision.MoveTo>(baseline);
        Assert.True(baseMove.Target.Band < current.Band, "baseline calm move should be DOWN in band");

        // With a recent manual UP nudge, the opposing down-move is penalised by W5 and must
        // no longer be chosen.
        var conflicted = DeterministicSetSelector.Select(
            current, Calm(), SelectorWeights.Default, recentManualLevelDelta: +1);

        if (conflicted is SelectorDecision.MoveTo m)
            Assert.True(m.Target.Band >= current.Band,
                $"a recent manual UP must suppress the band-DOWN move (got {m.Target})");
        // Hold is also an acceptable deferral.
    }

    [Fact]
    public void ManualConflict_AMoveAgreeingWithTheUser_IsNotSuppressed()
    {
        // Hot stats want UP; the user ALSO just went up (delta > 0). Agreement ⇒ no conflict
        // penalty ⇒ the up-move still fires.
        var current = new GridCoord(5, 2);
        var agreeing = DeterministicSetSelector.Select(
            current, HighIntensity(), SelectorWeights.Default, recentManualLevelDelta: +1);

        var move = Assert.IsType<SelectorDecision.MoveTo>(agreeing);
        Assert.True(move.Target.Band >= current.Band);
        // The conflict indicator itself is 0 when the move agrees with the user.
        Assert.Equal(0, DeterministicSetSelector.ManualConflict(
            current, new GridCoord(current.Band + 1, current.Shape), +1));
    }

    [Fact]
    public void ManualConflict_Indicator_IsOneOnlyForAnOpposingBandMove()
    {
        var c = new GridCoord(5, 2);

        // Opposing: user up, candidate down → 1.
        Assert.Equal(1, DeterministicSetSelector.ManualConflict(c, new GridCoord(4, 2), +1));
        // Opposing: user down, candidate up → 1.
        Assert.Equal(1, DeterministicSetSelector.ManualConflict(c, new GridCoord(6, 2), -1));
        // Agreeing: user up, candidate up → 0.
        Assert.Equal(0, DeterministicSetSelector.ManualConflict(c, new GridCoord(6, 2), +1));
        // No recent manual input → 0 regardless of direction.
        Assert.Equal(0, DeterministicSetSelector.ManualConflict(c, new GridCoord(4, 2), 0));
        // A pure-Shape move (no band change) → 0 (band is the intensity axis).
        Assert.Equal(0, DeterministicSetSelector.ManualConflict(c, new GridCoord(5, 3), +1));
    }

    [Fact]
    public void Fit_IsOneAtTarget_FallsLinearlyToZeroAtTolerance()
    {
        const double tol = 0.35;
        Assert.Equal(1.0, DeterministicSetSelector.Fit(0.5, 0.5, tol), 9);          // exact
        Assert.Equal(0.0, DeterministicSetSelector.Fit(0.5, 0.5 + tol, tol), 9);    // at edge
        Assert.Equal(0.0, DeterministicSetSelector.Fit(0.5, 0.5 + 2 * tol, tol), 9);// beyond → clamps
        Assert.Equal(0.5, DeterministicSetSelector.Fit(0.5, 0.5 + tol / 2, tol), 9);// halfway
        // Symmetry.
        Assert.Equal(
            DeterministicSetSelector.Fit(0.5, 0.7, tol),
            DeterministicSetSelector.Fit(0.7, 0.5, tol), 9);
    }

    [Fact]
    public void Selector_IsDeterministic_SameInputsSameDecision()
    {
        var current = new GridCoord(3, 1);
        var a = DeterministicSetSelector.Select(current, HighIntensity(), SelectorWeights.Default, 0);
        var b = DeterministicSetSelector.Select(current, HighIntensity(), SelectorWeights.Default, 0);
        Assert.Equal(a, b); // records are value-equatable
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. STATE MACHINE
    // ──────────────────────────────────────────────────────────────────────────

    // Small, explicit timings keep the dt arithmetic trivial and decoupled from the
    // production SenseDefaults values.
    private const double SettleMin = 10.0;
    private const double Cooldown  = 20.0;
    private const int    MinSamp   = 12;
    private const double DeclineX  = 2.0;

    private static AdaptiveFitStateMachine NewMachine() =>
        new(settlingMinSeconds: SettleMin, cooldownSeconds: Cooldown, minSamples: MinSamp,
            declineMultiplier: DeclineX);

    [Fact]
    public void StartsInDefault_AndCannotEvaluate()
    {
        var sm = NewMachine();
        Assert.Equal(CooldownState.Default, sm.CurrentState);
        Assert.False(sm.CanEvaluate);
    }

    [Fact]
    public void FullCycle_DefaultSettlingActiveCooldownActive()
    {
        var sm = NewMachine();

        // First tick leaves Default → Settling (settle floor not yet met).
        sm.Advance(dt: 1.0, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Settling, sm.CurrentState);
        Assert.False(sm.CanEvaluate);

        // Accumulate past the settle floor WITH samples + a confident pick → Active.
        sm.Advance(dt: SettleMin, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Active, sm.CurrentState);
        Assert.True(sm.CanEvaluate);

        // A set change enters Cooldown (and disables evaluation).
        sm.OnSetChange();
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        Assert.False(sm.CanEvaluate);
        Assert.Equal(Cooldown, sm.CooldownRemaining, 9);

        // The cooldown elapses → back to Active (soft-定型, re-eligible).
        sm.Advance(dt: Cooldown, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Active, sm.CurrentState);
        Assert.True(sm.CanEvaluate);
    }

    [Fact]
    public void Settling_DoesNotPromote_WithoutSamplesOrConfidentPickOrSettleTime()
    {
        // Enough time but too few samples → stays Settling.
        var noSamples = NewMachine();
        noSamples.Advance(dt: SettleMin + 5, sampleCount: MinSamp - 1, haveConfidentPick: true);
        Assert.Equal(CooldownState.Settling, noSamples.CurrentState);

        // Enough time + samples but NO confident pick → stays Settling.
        var noPick = NewMachine();
        noPick.Advance(dt: SettleMin + 5, sampleCount: MinSamp, haveConfidentPick: false);
        Assert.Equal(CooldownState.Settling, noPick.CurrentState);

        // Samples + pick but settle floor not yet met → stays Settling.
        var tooSoon = NewMachine();
        tooSoon.Advance(dt: SettleMin / 2, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Settling, tooSoon.CurrentState);
    }

    [Fact]
    public void DualTrigger_ManualAdjust_ResetsTheCooldownTimer_ButStaysInCooldown()
    {
        var sm = NewMachine();
        DriveToActive(sm);
        sm.OnSetChange();                 // enter Cooldown @ full
        sm.Advance(dt: Cooldown - 2, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        Assert.Equal(2.0, sm.CooldownRemaining, 9); // almost elapsed

        // Manual adjust RESETS the timer to full but KEEPS us in Cooldown (no state change).
        sm.OnManualAdjust();
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        Assert.Equal(Cooldown, sm.CooldownRemaining, 9);
    }

    [Fact]
    public void DualTrigger_SetChange_EntersCooldown_FromActive()
    {
        var sm = NewMachine();
        DriveToActive(sm);
        Assert.Equal(CooldownState.Active, sm.CurrentState);

        sm.OnSetChange();
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        Assert.Equal(Cooldown, sm.CooldownRemaining, 9);
    }

    [Fact]
    public void DualTrigger_ProposalDeclined_DoublesTheRemainingCooldown()
    {
        var sm = NewMachine();
        DriveToActive(sm);
        sm.OnSetChange();                 // Cooldown @ 20
        sm.Advance(dt: 5, sampleCount: MinSamp, haveConfidentPick: true); // remaining 15

        double before = sm.CooldownRemaining;
        Assert.Equal(15.0, before, 9);

        sm.OnProposalDeclined();          // ×2 → 30
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        Assert.Equal(before * DeclineX, sm.CooldownRemaining, 9);

        // A second decline doubles again (escalating back-off).
        sm.OnProposalDeclined();          // ×2 → 60
        Assert.Equal(before * DeclineX * DeclineX, sm.CooldownRemaining, 9);
    }

    [Fact]
    public void DualTrigger_Precedence_SetChangeBeatsManualBeatsDecline_Independence()
    {
        // The three triggers have DISTINCT, non-overlapping effects; this pins each shape:
        //   set-change  → state := Cooldown, remaining := full
        //   manual      → remaining := full, state unchanged
        //   decline     → remaining := remaining × multiplier (state := Cooldown if not already)
        var sm = NewMachine();
        DriveToActive(sm);

        // set-change is the ONLY trigger that (re)enters Cooldown from Active.
        sm.OnSetChange();
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);

        // burn most of it, then manual RESETS (not doubles, not elapses).
        sm.Advance(dt: 18, sampleCount: MinSamp, haveConfidentPick: true); // remaining 2
        sm.OnManualAdjust();
        Assert.Equal(Cooldown, sm.CooldownRemaining, 9);

        // burn again, then decline DOUBLES the remainder.
        sm.Advance(dt: 10, sampleCount: MinSamp, haveConfidentPick: true); // remaining 10
        sm.OnProposalDeclined();
        Assert.Equal(20.0, sm.CooldownRemaining, 9);
    }

    [Fact]
    public void NoOscillation_AlternatingBorderlineStatsCannotMoveTwiceWithinOneCooldown()
    {
        // After a set change the machine is in Cooldown and CanEvaluate is FALSE for the whole
        // SetCooldownSeconds window — so even alternating stats that would each prefer the
        // opposite neighbour cannot drive an A→B→A ping-pong inside one cooldown.
        var sm = NewMachine();
        DriveToActive(sm);
        sm.OnSetChange(); // pretend we just moved A→B

        // Feed many ticks that never fully elapse the cooldown; CanEvaluate stays false the
        // entire time, so no second move is ever permitted within the window.
        double fed = 0;
        while (fed < Cooldown - 1)
        {
            sm.Advance(dt: 1.0, sampleCount: MinSamp, haveConfidentPick: true);
            fed += 1.0;
            Assert.False(sm.CanEvaluate, $"must not be evaluable mid-cooldown (t={fed})");
            Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        }

        // Only once the full window elapses does it become evaluable again.
        sm.Advance(dt: 2.0, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Active, sm.CurrentState);
        Assert.True(sm.CanEvaluate);
    }

    [Fact]
    public void Advance_NeverRewinds_OnNegativeDt()
    {
        var sm = NewMachine();
        DriveToActive(sm);
        sm.OnSetChange();
        double before = sm.CooldownRemaining;

        sm.Advance(dt: -100, sampleCount: MinSamp, haveConfidentPick: true);
        // Negative dt is clamped to 0 — the timer neither grows nor the state flips.
        Assert.Equal(before, sm.CooldownRemaining, 9);
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
    }

    [Fact]
    public void ProposalDeclined_OutsideCooldown_StillEntersAnEscalatedCooldown()
    {
        var sm = NewMachine();
        DriveToActive(sm); // in Active, no timer running

        sm.OnProposalDeclined();
        Assert.Equal(CooldownState.Cooldown, sm.CurrentState);
        // Seeded at the doubled base so a decline always yields a real back-off.
        Assert.Equal(Cooldown * DeclineX, sm.CooldownRemaining, 9);
    }

    /// <summary>Drive a fresh machine all the way to <see cref="CooldownState.Active"/>.</summary>
    private static void DriveToActive(AdaptiveFitStateMachine sm)
    {
        sm.Advance(dt: SettleMin + 1, sampleCount: MinSamp, haveConfidentPick: true);
        Assert.Equal(CooldownState.Active, sm.CurrentState);
    }
}
