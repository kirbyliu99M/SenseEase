using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 Phase-0 protection-integrity pins (docs/BETA_GATE_REVIEW_2026-06-14.md #5).
/// These assert that vestibular personalization actually reaches the VISIBLE protection —
/// the safety gap where the personalized EffectiveTrigger (50) was computed but the mask /
/// FOV / tier were wired to the fixed TriggerPressure (85), so a vestibular user's visible
/// protection was byte-identical to a non-vestibular user.
///
/// Pressure model (LeakyIntegrator): dt = 1.0 s/tick when no clock is passed; stimulus =
/// OFI·PassiveFlowWeight(2) + QuadraticTerm(0.0008)·OFI²; alphaDt = BaseDecayAlpha(0.965)^10
/// ≈ 0.70. At OFI 22, P rises 44.4 → 75.5 → 97.3 over ticks 1-3, so there is a tick where
/// pressure sits in (50, 85): the mask is ON for a vestibular (trigger 50) engine but still
/// OFF for the default (trigger 85) engine — the discriminating window.
/// </summary>
public class ProtectionIntegrityPhase0Tests
{
    private const double RampOfi = 22.0;

    [Fact]
    public void Beta1_5_VestibularTrigger_EngagesMaskAtLowerPressureThanDefault()
    {
        var def = new MockInferenceEngine();
        var ves = new MockInferenceEngine();
        ves.SetVestibularProtection(true);   // EffectiveTrigger 85 -> 50

        // Note: the engine has an 8 s re-activation cooldown but NO cooldown on the OFF
        // transition, and the mask attenuates OFI once active — so in a fast (real-time)
        // loop a mask can flip OFF and not re-arm. We therefore track "ever activated"
        // edges, not the final steady state.
        bool sawVestibularOnWhileDefaultOff = false;
        bool defaultEverActivated = false;
        for (int i = 1; i <= 8; i++)
        {
            bool defMask = def.Update(RampOfi, "Passenger").MaskActive;
            bool vesMask = ves.Update(RampOfi, "Passenger").MaskActive;
            if (defMask) defaultEverActivated = true;
            if (vesMask && !defMask) sawVestibularOnWhileDefaultOff = true;
        }

        Assert.True(sawVestibularOnWhileDefaultOff,
            "vestibular protection (trigger 50) must engage the mask while the default (trigger 85) has not — proves EffectiveTrigger is wired into mask onset, not the fixed 85");
        Assert.True(defaultEverActivated,
            "the default mask must also engage under sustained OFI (proves the window above was a real ordering, not the default simply never activating)");
    }

    [Fact]
    public void Beta1_5_VestibularFovIsTighterThanDefault_AtTheSamePressure()
    {
        // One tick, identical input: both engines hold the SAME pressure (mask not yet active),
        // so any FOV difference is purely the trigger denominator. The vestibular engine's
        // lower trigger makes the clear zone strictly tighter — the FOV half of finding #5.
        var def = new MockInferenceEngine();
        var ves = new MockInferenceEngine();
        ves.SetVestibularProtection(true);

        int defFov = def.Update(RampOfi, "Passenger").FovRadiusPercent;
        int vesFov = ves.Update(RampOfi, "Passenger").FovRadiusPercent;

        Assert.True(vesFov < defFov,
            $"vestibular FOV must be tighter at equal pressure (trigger 50 < 85): vestibular={vesFov}, default={defFov}");
    }

    [Fact]
    public void Beta1_5_DefaultUser_IsByteIdentical_NonVestibularRegressionGuard()
    {
        // The fix must be byte-identical for the default user: EffectiveTrigger == 85, so a
        // vestibular engine toggled back OFF must match a fresh one tick-for-tick (mask + FOV).
        var a = new MockInferenceEngine();
        var b = new MockInferenceEngine();
        b.SetVestibularProtection(true);
        b.SetVestibularProtection(false);    // reverts EffectiveTrigger to the 85 default

        for (int i = 1; i <= 12; i++)
        {
            var sa = a.Update(RampOfi, "Passenger");
            var sb = b.Update(RampOfi, "Passenger");
            Assert.Equal(sa.MaskActive, sb.MaskActive);
            Assert.Equal(sa.FovRadiusPercent, sb.FovRadiusPercent);
            Assert.Equal(sa.VimsTier, sb.VimsTier);
        }
    }
}
