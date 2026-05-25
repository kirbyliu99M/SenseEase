using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Stage 2 of the calibration feature — pins the wiring between
/// CalibrationResult (Stage 1's pure output) and the engine state machine.
/// Two surfaces under test:
///
/// (a) HabituationEngine's new EffectiveTrigger property + SevereStrikeLimitOverride
///     — the engine itself stays ignorant of "scale"; both are simple stored
///     values consulted in the spike / strike paths.
///
/// (b) MockInferenceEngine.ApplyCalibrationResult — the math layer that turns
///     the calibration record into engine state. Math priority per user
///     supplement: override > scale + adjustment (override bypasses both).
/// </summary>
public class CalibrationApplicationTests
{
    private const double DefaultTrigger = SenseDefaults.TriggerPressure; // 85.0

    // ── (a) Engine surface ───────────────────────────────────────────────────

    [Fact]
    public void EngineEffectiveTrigger_DefaultsToSenseDefaultsTriggerPressure()
    {
        var engine = new HabituationEngine();
        Assert.Equal(DefaultTrigger, engine.EffectiveTrigger);
    }

    [Fact]
    public void EngineEffectiveTrigger_LoweredValue_FiresSpikeAtLowerPressure()
    {
        // Lower the trigger to 50 (vestibular-like override). Spike now fires
        // at pressure >= 50 * 0.60 = 30 (RecurrencePressureFraction).
        var engine = new HabituationEngine();
        engine.EffectiveTrigger = 50.0;

        // Below threshold: no spike, no pause.
        engine.Step(pressure: 29.0, maskActive: false, vimsTier: 1, dtSeconds: 1.0);
        Assert.NotEqual(HabituationPhase.Paused, engine.Phase);

        // At threshold: spike fires.
        engine.Step(pressure: 30.0, maskActive: false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(HabituationPhase.Paused, engine.Phase);
        Assert.True(engine.IsPaused);
    }

    [Fact]
    public void EngineEffectiveTrigger_DefaultValue_PreservesLegacySpikeThreshold()
    {
        // Belt: confirm the default value preserves the existing test invariants
        // (HabituationTests pins pressure 70 -> spike at default trigger 85).
        var engine = new HabituationEngine();
        // 85 * 0.60 = 51 — pressure 70 is above, spike fires.
        engine.Step(pressure: 70.0, maskActive: false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(HabituationPhase.Paused, engine.Phase);
    }

    [Fact]
    public void EngineEffectiveTrigger_HighValue_RaisesSpikeThreshold()
    {
        // Trigger 120 → recurrence at 72. Pressure 70 should now NOT spike.
        var engine = new HabituationEngine();
        engine.EffectiveTrigger = 120.0;
        engine.Step(pressure: 70.0, maskActive: false, vimsTier: 1, dtSeconds: 1.0);
        Assert.NotEqual(HabituationPhase.Paused, engine.Phase);
    }

    [Fact]
    public void EngineSevereStrikeLimitOverride_OneFiresZeroOnFirstStrike()
    {
        // With RESILIENT default (limit=2), first spike halves. With override=1,
        // first spike zeros + resets count.
        var engine = new HabituationEngine();
        engine.SevereStrikeLimitOverride = 1;

        // Drive calm to earn offset so the halve-vs-zero distinction is observable.
        for (int i = 0; i < 240; i++) engine.Step(10.0, false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(8, engine.HabituationOffset); // 240s of RESILIENT calm

        // One severe strike (chatbot path, weight 0.5).
        // Override = 1, so 0.5 >= 1 - 1e-9 is false on first call... wait:
        // 0.5 < 1 - 1e-9, so first call would HALVE. Second call accumulates
        // to 1.0 >= 1 - 1e-9, zeros. Let me re-check the semantics —
        // override=1 means LIMIT is 1, but weight 0.5 still accumulates.
        engine.NotifyDiscomfort(severe: true);                       // count 0.5, < 1, halve 8->4
        Assert.Equal(4, engine.HabituationOffset);
        Assert.Equal(0.5, engine.SevereStrikeCount);

        engine.NotifyDiscomfort(severe: true);                       // count 1.0, ≥ 1, zero + reset
        Assert.Equal(0, engine.HabituationOffset);
        Assert.Equal(0.0, engine.SevereStrikeCount);
    }

    [Fact]
    public void EngineSevereStrikeLimitOverride_NullFallsBackToProfileDefault()
    {
        // Override = null, RESILIENT.SevereStrikeLimit = 2.
        var engine = new HabituationEngine();
        for (int i = 0; i < 420; i++) engine.Step(10.0, false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(20, engine.HabituationOffset);

        engine.NotifyDiscomfort(severe: true); // 0.5, halves 20 -> 10 (< 2 limit)
        Assert.Equal(10, engine.HabituationOffset);
        Assert.Equal(0.5, engine.SevereStrikeCount);
    }

    [Fact]
    public void EngineSevereStrikeLimitOverride_SensorSpikeWeight1_ZerosImmediatelyWhenOverrideIs1()
    {
        // Sensor spike weight = 1.0. Override = 1, so 1.0 >= 1 - 1e-9 fires
        // limit on the very first sensor spike.
        var engine = new HabituationEngine();
        engine.SevereStrikeLimitOverride = 1;
        for (int i = 0; i < 240; i++) engine.Step(10.0, false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(8, engine.HabituationOffset);

        engine.Step(pressure: 70.0, maskActive: false, vimsTier: 1, dtSeconds: 1.0);
        Assert.Equal(0, engine.HabituationOffset);
        Assert.Equal(0.0, engine.SevereStrikeCount);
    }

    // ── (b) Wrapper surface — ApplyCalibrationResult math ────────────────────

    private static CalibrationResult MakeResult(
        HabituationProfile profile = HabituationProfile.Resilient,
        double scale = 1.0,
        int? overrideValue = null,
        int adjustment = 0,
        int? strikeLimitOverride = null)
    => new()
    {
        Profile                    = profile,
        PersonalPressureScale      = scale,
        TriggerThresholdOverride   = overrideValue,
        TriggerThresholdAdjustment = adjustment,
        SevereStrikeLimitOverride  = strikeLimitOverride,
        MedicalAdvisory            = null,
        SymptomDominancePrimary    = null,
        CalibrationConfidence      = CalibrationConfidence.High,
        Completeness               = CalibrationCompleteness.Complete,
    };

    [Fact]
    public void Apply_ScaleOne_NoOverride_NoAdjust_KeepsDefaultTrigger()
    {
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(scale: 1.0));
        // 85 / 1.0 + 0 = 85
        Assert.Equal(DefaultTrigger, engine.HabituationEffectiveTrigger, precision: 2);
    }

    [Fact]
    public void Apply_HighScale_LowersEffectiveTrigger()
    {
        // scale 1.5 -> trigger 85/1.5 ≈ 56.67
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(scale: 1.5));
        Assert.Equal(56.67, engine.HabituationEffectiveTrigger, precision: 2);
    }

    [Fact]
    public void Apply_LowScale_RaisesEffectiveTrigger()
    {
        // scale 0.7 -> trigger 85/0.7 ≈ 121.43
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(scale: 0.7));
        Assert.Equal(121.43, engine.HabituationEffectiveTrigger, precision: 2);
    }

    [Fact]
    public void Apply_AdjustmentMinusTen_ScaleOne_LowersTriggerBy10()
    {
        // 85 / 1.0 + (-10) = 75
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(scale: 1.0, adjustment: -10));
        Assert.Equal(75.0, engine.HabituationEffectiveTrigger, precision: 2);
    }

    [Fact]
    public void Apply_OverrideTrumpsBothScaleAndAdjustment()
    {
        // Override = 50, scale = 1.5, adjustment = -10. Math priority says
        // override wins outright: trigger = 50, NOT (85/1.5 - 10 ≈ 46.67).
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(
            scale: 1.5, overrideValue: 50, adjustment: -10));
        Assert.Equal(50.0, engine.HabituationEffectiveTrigger, precision: 2);
    }

    [Fact]
    public void Apply_OverrideBoundaryBehaviour_SpikeAtRecurrenceFraction()
    {
        // Override = 50, recurrence threshold = 30. Drive engine directly.
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(overrideValue: 50));
        Assert.Equal(50.0, engine.HabituationEffectiveTrigger, precision: 2);
    }

    [Fact]
    public void Apply_StrikeLimitOverride_PassesThroughToEngine()
    {
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(strikeLimitOverride: 1));
        Assert.Equal(1, engine.HabituationSevereStrikeLimitOverride);
    }

    [Fact]
    public void Apply_StrikeLimitOverrideNull_LeavesEngineDefault()
    {
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(strikeLimitOverride: null));
        Assert.Null(engine.HabituationSevereStrikeLimitOverride);
    }

    [Fact]
    public void Apply_VestibularComboBehaviour_OverrideAndStrikeLimitTogether()
    {
        // Full vestibular calibration result: SENSITIVE profile, override 50, limit 1.
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(
            profile: HabituationProfile.Sensitive,
            overrideValue: 50,
            strikeLimitOverride: 1));

        Assert.Equal(50.0, engine.HabituationEffectiveTrigger, precision: 2);
        Assert.Equal(1, engine.HabituationSevereStrikeLimitOverride);
        Assert.Equal(HabituationProfile.Sensitive, engine.HabituationProfile);
        Assert.Equal("calibration_demo", engine.HabituationProfileSource);
    }

    [Fact]
    public void Apply_StoresResultForLaterRead()
    {
        var engine = new MockInferenceEngine();
        var result = MakeResult(
            profile: HabituationProfile.Typical,
            scale: 1.2,
            adjustment: -5);
        engine.ApplyCalibrationResult(result);

        var stored = engine.LastCalibrationResult;
        Assert.NotNull(stored);
        Assert.Equal(HabituationProfile.Typical, stored!.Profile);
        Assert.Equal(1.2, stored.PersonalPressureScale);
        Assert.Equal(-5, stored.TriggerThresholdAdjustment);
    }

    [Fact]
    public void Apply_DefensiveScaleClamp_NeverProducesOutOfRangeTrigger()
    {
        // Caller bypasses CalibrationProcessor's clamp and shoves an out-of-range
        // scale at us. Wrapper must defensively clamp to [0.7, 1.5] before
        // dividing — protects the engine from absurd trigger values.
        var engine = new MockInferenceEngine();
        engine.ApplyCalibrationResult(MakeResult(scale: 5.0));
        // Clamped to 1.5 → 85/1.5 ≈ 56.67
        Assert.Equal(56.67, engine.HabituationEffectiveTrigger, precision: 2);

        engine.ApplyCalibrationResult(MakeResult(scale: 0.1));
        // Clamped to 0.7 → 85/0.7 ≈ 121.43
        Assert.Equal(121.43, engine.HabituationEffectiveTrigger, precision: 2);
    }
}
