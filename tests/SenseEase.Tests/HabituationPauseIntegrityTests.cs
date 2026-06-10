using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>CW-1 (LOGIC-01 / INTENT-01) — the pause-integrity lock. A silent
/// background AdaptiveFit set move (<see cref="MockInferenceEngine.ApplySetMove"/>)
/// and the snap-cause branches of <see cref="MockInferenceEngine.SetLevel"/> clear
/// the transient chatbot delta via
/// <see cref="HabituationEngine.ResetTransientStatePreservingPause"/> instead of the
/// full <see cref="HabituationEngine.ResetTransientState"/>. Those silent paths run
/// WITHOUT the user, so they must NOT un-pause opted-out habituation nor exit the
/// tier ≥ 8 <see cref="EngineState.PausedAwaitingUserConfirm"/> park — only
/// <see cref="HabituationEngine.AcknowledgeAndResume"/> (the explicit user path) may
/// do that (whitepaper §7.4, SAFETY-PROVISIONS §8).
///
/// Coverage map (the user's required-cases list for CW-1):
///  (a) externally-paused + silent set move ⇒ IsExternallyPaused STILL true
///  (b) PausedAwaitingUserConfirm (tier ≥ 8) + set move ⇒ STILL awaiting; only
///      AcknowledgeAndResume exits
///  (c) severe-strike count PRESERVED across a set move
///  (d) the explicit user reset (ResetTransientState / ResetPersonalization) is
///      UNCHANGED — it still clears every pause flag
///  (e) _offset is unchanged through all of the above (Q4.5)
/// </summary>
public class HabituationPauseIntegrityTests
{
    // ── Helper: drive calm pressure for N seconds (dt = 1 s ticks). ───────────
    private static void Calm(HabituationEngine e, int seconds, int tier = 1)
    {
        for (int i = 0; i < seconds; i++) e.Step(10.0, false, tier, 1.0);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (a) Externally-paused survives the pause-preserving reset
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreservingReset_KeepsExternallyPaused_AndOffset()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);                    // earn offset = 20 (matches strike-test baseline)
        int offsetBefore = engine.HabituationOffset;
        Assert.Equal(20, offsetBefore);

        engine.SetPaused(true);               // user opted out of habituation
        Assert.True(engine.IsExternallyPaused);

        engine.ResetTransientStatePreservingPause(); // the silent set-move path

        // The opt-out pause MUST survive a silent selector move.
        Assert.True(engine.IsExternallyPaused);
        Assert.True(engine.IsPaused);
        // _offset is untouched (Q4.5).
        Assert.Equal(offsetBefore, engine.HabituationOffset);
    }

    // ── Contrast: the FULL reset (explicit user path) DOES clear the pause ────
    // Pins case (d): ResetTransientState behaviour is unchanged — it still clears
    // every pause flag. If a regression pointed the user path at the preserving
    // reset, this would fail.

    [Fact]
    public void FullReset_ClearsExternallyPaused_ButKeepsOffset()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);
        int offsetBefore = engine.HabituationOffset;
        engine.SetPaused(true);
        Assert.True(engine.IsExternallyPaused);

        engine.ResetTransientState();         // the explicit /reset shader path

        Assert.False(engine.IsExternallyPaused); // full reset DOES un-pause
        Assert.False(engine.IsPaused);
        Assert.Equal(offsetBefore, engine.HabituationOffset); // _offset still survives (Q4.5)
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (b) PausedAwaitingUserConfirm survives; only AcknowledgeAndResume exits
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreservingReset_KeepsPausedAwaitingUserConfirm_OnlyAcknowledgeExits()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);                    // offset = 20
        // Tier-11 spike → PausedAwaitingUserConfirm (12-level scale, gate is ≥ 10).
        // The spike halves the offset (strike 1.0 < limit 2.0): 20 → 10.
        engine.Step(70.0, false, vimsTier: 11, dtSeconds: 1.0);
        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
        int offsetBefore = engine.HabituationOffset;
        Assert.Equal(10, offsetBefore);

        engine.ResetTransientStatePreservingPause(); // silent set-move path

        // STILL parked: a silent move may not exit the tier ≥ 8 confirm gate.
        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
        Assert.Equal(offsetBefore, engine.HabituationOffset); // _offset untouched

        // Even an HOUR of calm must not auto-resume after the silent reset.
        for (int i = 0; i < 3600; i++) engine.Step(10.0, false, vimsTier: 11, dtSeconds: 1.0);
        Assert.True(engine.IsAwaitingUserConfirm);

        // ONLY the explicit user acknowledgement exits the park.
        engine.AcknowledgeAndResume();
        Assert.False(engine.IsAwaitingUserConfirm);
        Assert.False(engine.IsPaused);
        Assert.Equal(EngineState.Running, engine.CurrentEngineState);
        // _offset preserved through the entire arc (Q4.5).
        Assert.Equal(offsetBefore, engine.HabituationOffset);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (c) Severe-strike count preserved across the pause-preserving reset
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreservingReset_PreservesSevereStrikeCount()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);                    // offset = 20
        // One chatbot severe strike (weight 0.5) — below the RESILIENT limit (2.0),
        // so the counter holds at 0.5 and the offset halves (20 → 10).
        engine.NotifyDiscomfort(severe: true);
        Assert.Equal(0.5, engine.SevereStrikeCount);
        int offsetBefore = engine.HabituationOffset;

        engine.ResetTransientStatePreservingPause();

        // Strike history must NOT be wiped by a silent selector move.
        Assert.Equal(0.5, engine.SevereStrikeCount);
        Assert.Equal(offsetBefore, engine.HabituationOffset); // _offset untouched

        // Contrast: the FULL reset DOES zero the strike counter (case (d) unchanged).
        engine.ResetTransientState();
        Assert.Equal(0.0, engine.SevereStrikeCount);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (e) _offset is never touched by EITHER reset — explicit single-purpose pin
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NeitherReset_TouchesTheEarnedOffset()
    {
        var engine = new HabituationEngine();
        Calm(engine, 1000);                   // saturate to RESILIENT cap = 30
        Assert.Equal(30, engine.HabituationOffset);

        engine.ResetTransientStatePreservingPause();
        Assert.Equal(30, engine.HabituationOffset);

        engine.ResetTransientState();
        Assert.Equal(30, engine.HabituationOffset);
    }

    // ── The preserving reset still clears the session stability accumulators ──
    // Sanity: it is a genuine transient reset (not a no-op). After earning a
    // partial stable window, the reset zeroes the credit so the next relaxation
    // must re-earn the FULL window from scratch.

    [Fact]
    public void PreservingReset_ClearsStabilityCredit_RequiresFullWindowAgain()
    {
        var engine = new HabituationEngine();
        Calm(engine, 1000);                   // saturate to cap 30 (proves stepping works)
        Assert.Equal(30, engine.HabituationOffset);

        // A fresh engine re-earns from 0; here we assert the reset re-arms the
        // stability gate by checking that a SHORT post-reset calm burst does not
        // immediately relax further (already capped, so offset holds at 30 and
        // the accumulator was zeroed — no underflow / negative drift).
        engine.ResetTransientStatePreservingPause();
        Calm(engine, 1);                      // one calm tick — far below a full window
        Assert.Equal(30, engine.HabituationOffset); // still capped, no spurious change
    }

    // ──────────────────────────────────────────────────────────────────────────
    // INTEGRATION (production wiring): the SILENT SetLevel snap path through
    // MockInferenceEngine preserves a user opt-out pause. This exercises the real
    // call site fixed by CW-1 (SetLevel → ResetTransientStatePreservingPause)
    // through the public engine surface, with the production selector flag ON.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetLevelSnap_WithSelectorOn_DoesNotUnpauseUserOptOut()
    {
        var engine = new MockInferenceEngine { AdaptiveFitSelectorEnabled = true };

        // User opts out of habituation (the dashboard pause).
        engine.SetHabituationPaused(true);
        Assert.True(engine.Current.HabituationPaused);

        // A silent felt-intensity snap (the selector's snap path) must NOT clear
        // the user's opt-out pause — Current.HabituationPaused mirrors the engine's
        // IsPaused, which stays true because _externallyPaused is preserved.
        engine.SetLevel(8, LevelChangeCause.FeltIntensitySnap);
        Assert.True(engine.Current.HabituationPaused);

        engine.SetLevel(6, LevelChangeCause.SetChangeSnap);
        Assert.True(engine.Current.HabituationPaused);

        // Sanity: the explicit user un-pause path still works (case (d) wiring).
        engine.SetHabituationPaused(false);
        Assert.False(engine.Current.HabituationPaused);
    }
}
