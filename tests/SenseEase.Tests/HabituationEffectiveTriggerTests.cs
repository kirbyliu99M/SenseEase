using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pins the <see cref="HabituationEngine"/> effective-trigger +
/// strike-limit-override surface — the engine itself stays ignorant of "scale"; both
/// are simple stored values consulted in the spike / strike paths.
///
/// <para>These tests were carried over (verbatim) from the now-deleted
/// CalibrationApplicationTests surface (a) when D-13 removed the calibration
/// application plumbing. They test <see cref="HabituationEngine"/> DIRECTLY — they never
/// touched the deleted CalibrationProcessor.Process / MockInferenceEngine.ApplyCalibrationResult
/// path — so this coverage of the still-live EffectiveTrigger / SevereStrikeLimitOverride
/// API survives the deletion (the rest of CalibrationApplicationTests, which drove the
/// deleted wrapper, was dropped).</para></summary>
public class HabituationEffectiveTriggerTests
{
    private const double DefaultTrigger = SenseDefaults.TriggerPressure; // 85.0

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
}
