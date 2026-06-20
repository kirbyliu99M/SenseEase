using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Phase 3 tests covering the strike counter, tier-aware post-spike
/// pause, EngineState computed projection, and SetProfile / AcknowledgeAndResume
/// state-preservation contracts.
///
/// Coverage map (matches the user's required-cases list in the Phase 3 sign-off):
///  1. tier 1 spike → PostSpikePauseLow auto-resumes at 90 s (RESILIENT)
///  2. tier 7 spike → PostSpikePauseMid (RESILIENT 120 s)
///  3. tier 9 spike → awaitingUserConfirm = true, cooldown = +Infinity, no auto-resume
///  4. AcknowledgeAndResume() exits awaitingUserConfirm → Observing
///  5. 4 × chatbot severe in RESILIENT → zero (strike accumulation, weight 0.5)
///  6. SetProfile does NOT clear awaitingUserConfirm (supplement #2)
///  7. Full stable-window pass resets strike count
///  8. Profile change resets strikes, preserves _offset, preserves awaitingUserConfirm
/// </summary>
public class HabituationStrikeAndStateTests
{
    // ── Helper: drive calm pressure for N seconds (dt = 1 s ticks). ───────────
    private static void Calm(HabituationEngine e, int seconds, int tier = 1)
    {
        for (int i = 0; i < seconds; i++) e.Step(10.0, false, tier, 1.0);
    }

    // ── Case 1: tier 1 spike → PostSpikePauseLow (RESILIENT = 90 s) ──────────

    [Fact]
    public void Tier1Spike_AppliesPostSpikePauseLowAndAutoResumes()
    {
        var engine = new HabituationEngine();
        Calm(engine, 240); // earn some offset to confirm halving fires

        engine.Step(70.0, false, vimsTier: 1, dtSeconds: 1.0); // spike

        Assert.True(engine.IsPaused);
        Assert.False(engine.IsAwaitingUserConfirm);
        Assert.Equal(EngineState.Cooldown, engine.CurrentEngineState);

        // Auto-resume after PostSpikePauseLow (RESILIENT = 90 s). Drive 90 s
        // of mid-band pressure (not calm enough to relax, not high enough to
        // re-spike) and confirm pause clears.
        for (int i = 0; i < 90; i++) engine.Step(40.0, false, vimsTier: 1, dtSeconds: 1.0);
        Assert.False(engine.IsPaused);
        Assert.Equal(EngineState.Running, engine.CurrentEngineState);
    }

    // ── Case 2: tier 7 spike → PostSpikePauseMid (RESILIENT = 120 s) ─────────

    [Fact]
    public void Tier7Spike_AppliesPostSpikePauseMid()
    {
        var engine = new HabituationEngine();
        Calm(engine, 240);

        engine.Step(70.0, false, vimsTier: 7, dtSeconds: 1.0);

        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.Cooldown, engine.CurrentEngineState);
        // After 90 s (the LOW pause length) we should STILL be paused —
        // mid pause is 120 s on RESILIENT, so 90 s is not enough.
        for (int i = 0; i < 90; i++) engine.Step(40.0, false, vimsTier: 7, dtSeconds: 1.0);
        Assert.True(engine.IsPaused);
        // After another 30 s we cross the 120 s threshold.
        for (int i = 0; i < 30; i++) engine.Step(40.0, false, vimsTier: 7, dtSeconds: 1.0);
        Assert.False(engine.IsPaused);
    }

    // ── Case 3: level 11 spike → PausedAwaitingUserConfirm, never auto-resumes ─
    // (12-level scale: the confirm gate is level ≥ 10; 11 is comfortably inside it.)

    [Fact]
    public void Tier11Spike_EntersPausedAwaitingUserConfirmAndStays()
    {
        var engine = new HabituationEngine();
        Calm(engine, 240);

        engine.Step(70.0, false, vimsTier: 11, dtSeconds: 1.0);

        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);

        // Drive an HOUR of calm — engine must NOT auto-resume.
        for (int i = 0; i < 3600; i++) engine.Step(10.0, false, vimsTier: 11, dtSeconds: 1.0);
        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
    }

    // ── Case 3 (E4): level ≥ 10 confirm-gate, parameterised over profile × level ─
    // Generalises Case 3 above into a cross-product of {RESILIENT, TYPICAL,
    // SENSITIVE} × {level 10, 11, 12}. The level ≥ 10 user-confirm gate is
    // profile-INDEPENDENT (HabituationProfileParams.GetPostSpikePauseMs returns
    // requireUserConfirm = true for any vimsTier >= 10, regardless of the
    // profile's pause numerics), so every combination must park in
    // PausedAwaitingUserConfirm and never auto-resume. Assertions mirror Case 3
    // exactly — none are weakened.

    [Theory]
    [InlineData(HabituationProfile.Resilient, 10)]
    [InlineData(HabituationProfile.Resilient, 11)]
    [InlineData(HabituationProfile.Resilient, 12)]
    [InlineData(HabituationProfile.Adapting, 10)]
    [InlineData(HabituationProfile.Adapting, 11)]
    [InlineData(HabituationProfile.Adapting, 12)]
    [InlineData(HabituationProfile.Reactive, 10)]
    [InlineData(HabituationProfile.Reactive, 11)]
    [InlineData(HabituationProfile.Reactive, 12)]
    public void HighTierSpike_EntersPausedAwaitingUserConfirmAndStays_AcrossProfiles(
        HabituationProfile profile, int tier)
    {
        var engine = new HabituationEngine();
        // Apply the profile under test BEFORE earning any offset. A non-default
        // source guarantees SetProfile applies even for the RESILIENT default
        // (SetProfile no-ops only on same-profile AND same-source).
        engine.SetProfile(profile, "test_param");
        Calm(engine, 240, tier);

        engine.Step(70.0, false, vimsTier: tier, dtSeconds: 1.0);

        // Enters the confirm gate (mirrors Case 3, generalised to level ≥ 10).
        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);

        // Drive an HOUR of calm — engine must NOT auto-resume for any profile.
        for (int i = 0; i < 3600; i++) engine.Step(10.0, false, vimsTier: tier, dtSeconds: 1.0);
        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
    }

    // ── Case 3 negative control: level 9 never enters the confirm gate ────────
    // Level 9 is the highest level still below the confirm threshold (12-level
    // scale: gate is level ≥ 10). Across ALL profiles it must take the
    // auto-resuming mid-pause (Cooldown), NOT the user-confirm park. This pins
    // the boundary of the level ≥ 10 gate so a regression that lowered the
    // threshold to 9 would fail here.

    [Theory]
    [InlineData(HabituationProfile.Resilient)]
    [InlineData(HabituationProfile.Adapting)]
    [InlineData(HabituationProfile.Reactive)]
    public void Tier9Spike_NeverEntersConfirmGate_AcrossProfiles(HabituationProfile profile)
    {
        var engine = new HabituationEngine();
        engine.SetProfile(profile, "test_param");
        Calm(engine, 240, tier: 9);

        engine.Step(70.0, false, vimsTier: 9, dtSeconds: 1.0);

        // Auto-resuming cooldown — explicitly NOT the confirm gate.
        Assert.False(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.Cooldown, engine.CurrentEngineState);
        Assert.NotEqual(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
    }

    // ── Case 4: AcknowledgeAndResume returns from awaiting state ─────────────

    [Fact]
    public void AcknowledgeAndResume_ExitsAwaitingUserConfirmToObserving()
    {
        var engine = new HabituationEngine();
        Calm(engine, 240);
        engine.Step(70.0, false, vimsTier: 11, dtSeconds: 1.0);
        Assert.True(engine.IsAwaitingUserConfirm);

        engine.AcknowledgeAndResume();

        Assert.False(engine.IsAwaitingUserConfirm);
        Assert.False(engine.IsPaused);
        Assert.Equal(HabituationPhase.Observing, engine.Phase);
        Assert.Equal(EngineState.Running, engine.CurrentEngineState);
    }

    [Fact]
    public void AcknowledgeAndResume_NoOpWhenNotAwaiting()
    {
        var engine = new HabituationEngine();
        Calm(engine, 240);
        // Engine is Observing, not paused. AcknowledgeAndResume must not
        // mutate anything.
        int offsetBefore = engine.HabituationOffset;
        engine.AcknowledgeAndResume();
        Assert.Equal(offsetBefore, engine.HabituationOffset);
        Assert.False(engine.IsPaused);
    }

    // ── Case 5: 4 × chatbot severe in RESILIENT → zero ───────────────────────

    [Fact]
    public void FourChatbotSevere_AccumulatesToStrikeLimitAndZeros()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);
        Assert.Equal(20, engine.HabituationOffset);

        // Call 1: count 0 → 0.5, offset halved (20 → 10)
        engine.NotifyDiscomfort(severe: true);
        Assert.Equal(10, engine.HabituationOffset);
        Assert.Equal(0.5, engine.SevereStrikeCount);

        // Call 2: count 0.5 → 1.0, offset halved (10 → 5)
        engine.NotifyDiscomfort(severe: true);
        Assert.Equal(5, engine.HabituationOffset);
        Assert.Equal(1.0, engine.SevereStrikeCount);

        // Call 3: count 1.0 → 1.5, offset halved (5 → 2.5 → rounds to 2 via
        // Math.Round's default banker's rounding — MidpointRounding.ToEven).
        engine.NotifyDiscomfort(severe: true);
        Assert.Equal(2, engine.HabituationOffset);
        Assert.Equal(1.5, engine.SevereStrikeCount);

        // Call 4: count 1.5 → 2.0 = RESILIENT.SevereStrikeLimit, zeros + resets.
        engine.NotifyDiscomfort(severe: true);
        Assert.Equal(0, engine.HabituationOffset);
        Assert.Equal(0.0, engine.SevereStrikeCount); // counter reset
    }

    // ── Case 6: SetProfile preserves awaitingUserConfirm (supplement #2) ─────

    [Fact]
    public void SetProfile_PreservesAwaitingUserConfirmAndCooldown()
    {
        var engine = new HabituationEngine();
        Calm(engine, 240);
        engine.Step(70.0, false, vimsTier: 11, dtSeconds: 1.0);
        Assert.True(engine.IsAwaitingUserConfirm);

        // User (via chatbot) switches profile mid-pause.
        engine.SetProfile(HabituationProfile.Reactive, "test_param");

        // Pause state MUST survive — current safety requirement is orthogonal
        // to which profile they'll resume into.
        Assert.True(engine.IsAwaitingUserConfirm);
        Assert.True(engine.IsPaused);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
        // Profile change DID take effect for the profile fields.
        Assert.Equal(HabituationProfile.Reactive, engine.CurrentProfile);
        Assert.Equal("test_param", engine.ProfileSource);
    }

    // ── Case 7: stable window pass resets strike count ──────────────────────

    [Fact]
    public void StableWindowPass_ResetsStrikeCount()
    {
        var engine = new HabituationEngine();
        // Earn one strike.
        Calm(engine, 240); // offset = 20
        engine.NotifyDiscomfort(severe: true);
        Assert.Equal(0.5, engine.SevereStrikeCount);

        // The NotifyDiscomfort call entered a 90 s pause. Wait it out.
        // Pause clears around 90 s of any pressure (not calm enough to relax).
        for (int i = 0; i < 90; i++) engine.Step(40.0, false, vimsTier: 1, dtSeconds: 1.0);
        Assert.False(engine.IsPaused);

        // Now drive a full stable window (120 s of CALM, not just any pressure).
        // The strike-count reset fires INSIDE Step when _stableElapsed >= _effectiveWindow.
        Calm(engine, 120);

        Assert.Equal(0.0, engine.SevereStrikeCount);
    }

    // ── Case 8: profile change resets strikes, preserves offset + awaiting ──

    [Fact]
    public void SetProfile_ResetsStrikes_PreservesOffset_PreservesAwaitingFlag()
    {
        var engine = new HabituationEngine();
        Calm(engine, 420);                                          // _offset = 20 (matches legacy test baseline)
        engine.NotifyDiscomfort(severe: true);                      // strike 0.5, offset 10
        engine.NotifyDiscomfort(severe: true);                      // strike 1.0, offset 5
        Assert.Equal(5, engine.HabituationOffset);
        Assert.Equal(1.0, engine.SevereStrikeCount);

        int offsetBefore = engine.HabituationOffset;
        engine.SetProfile(HabituationProfile.Adapting, "test_param");

        Assert.Equal(0.0, engine.SevereStrikeCount);                // strikes RESET
        Assert.Equal(offsetBefore, engine.HabituationOffset);       // offset PRESERVED
        // Awaiting flag was never set in this scenario; verify it's still false.
        Assert.False(engine.IsAwaitingUserConfirm);
    }

    [Fact]
    public void SetProfile_ResetsStrikes_ClampsOffsetToNewCapWhenLower()
    {
        var engine = new HabituationEngine(); // RESILIENT cap = 30
        Calm(engine, 1000); // saturate to cap 30
        Assert.Equal(30, engine.HabituationOffset);

        // SENSITIVE cap = 15. Offset must be clamped down.
        engine.SetProfile(HabituationProfile.Reactive, "test_param");

        Assert.Equal(15, engine.HabituationOffset);
    }

    // ── Bonus: EngineState computed projection correctness ───────────────────

    [Fact]
    public void CurrentEngineState_Running_WhenNoPause()
    {
        var engine = new HabituationEngine();
        Calm(engine, 5);
        Assert.Equal(EngineState.Running, engine.CurrentEngineState);
    }

    [Fact]
    public void CurrentEngineState_Cooldown_AfterLowTierSpike()
    {
        var engine = new HabituationEngine();
        engine.Step(70.0, false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(EngineState.Cooldown, engine.CurrentEngineState);
    }

    [Fact]
    public void CurrentEngineState_PausedAwaiting_AfterTier11Spike()
    {
        var engine = new HabituationEngine();
        engine.Step(70.0, false, vimsTier: 11, dtSeconds: 1.0);
        Assert.Equal(EngineState.PausedAwaitingUserConfirm, engine.CurrentEngineState);
    }

    [Fact]
    public void ExternallyPaused_DoesNotMaskCooldownInEngineState()
    {
        // Q8 supplement: externally_paused is parallel to engine_state, not
        // a mask. Even if the user has manually paused, the engine_state must
        // surface the underlying spike-cooldown fact to the chatbot.
        var engine = new HabituationEngine();
        engine.Step(70.0, false, vimsTier: 1, dtSeconds: 1.0);
        engine.SetPaused(true);

        Assert.True(engine.IsExternallyPaused);
        Assert.Equal(EngineState.Cooldown, engine.CurrentEngineState);
    }

    // ── Backward-compat shim — Phase 2-era tests using Step(p, m, dt) ───────

    [Fact]
    public void LegacyStep_3ArgOverload_DefaultsToTier1Behaviour()
    {
        var engine = new HabituationEngine();
#pragma warning disable CS0618 // intentional use of the [Obsolete] shim
        engine.Step(70.0, false, dtSeconds: 1.0);
#pragma warning restore CS0618
        // tier 1 → PostSpikePauseLow, Cooldown (not PausedAwaitingUserConfirm).
        Assert.Equal(EngineState.Cooldown, engine.CurrentEngineState);
        Assert.False(engine.IsAwaitingUserConfirm);
    }

    // ── ProfileChanged event fires on SetProfile, not on no-op repeat ───────

    [Fact]
    public void ProfileChanged_FiresOnceOnSetProfile()
    {
        var engine = new HabituationEngine();
        int callCount = 0;
        HabituationProfile? lastProfile = null;
        string? lastSource = null;
        engine.ProfileChanged += (p, s) =>
        {
            callCount++;
            lastProfile = p;
            lastSource = s;
        };

        engine.SetProfile(HabituationProfile.Adapting, "calibration_demo");
        engine.SetProfile(HabituationProfile.Adapting, "calibration_demo"); // no-op, same profile + source

        Assert.Equal(1, callCount);
        Assert.Equal(HabituationProfile.Adapting, lastProfile);
        Assert.Equal("calibration_demo", lastSource);
    }
}
