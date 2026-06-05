using System;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Unit tests for the Phase-1 inference logic (Leaky Integrator + mask thresholding).</summary>
public class InferenceTests
{
    [Fact]
    public void NoiseGate_DiscardsSubThresholdFlow()
    {
        var integrator = new LeakyIntegrator();

        double pressure = integrator.Step(SenseDefaults.NoiseGateThreshold - 1, 1.0);

        Assert.Equal(0.0, pressure);
    }

    [Fact]
    public void LeakyIntegrator_AccumulatesPressureAcrossSteps()
    {
        var integrator = new LeakyIntegrator();

        double first = integrator.Step(100, 1.0);
        double second = integrator.Step(100, 1.0);

        Assert.True(first > 0);
        Assert.True(second > first);
    }

    [Fact]
    public void LeakyIntegrator_DecaysWhenInputStops()
    {
        var integrator = new LeakyIntegrator();
        for (int i = 0; i < 10; i++)
        {
            integrator.Step(120, 1.5);
        }

        double peak = integrator.Pressure;
        double afterIdle = integrator.Step(0, 1.5); // gated to zero -> only decay

        Assert.True(afterIdle < peak);
    }

    [Fact]
    public void MockInferenceEngine_ActivatesMaskAboveTriggerPressure()
    {
        var engine = new MockInferenceEngine();

        SenseState state = engine.Current;
        for (int i = 0; i < 200 && !state.MaskActive; i++)
        {
            state = engine.Update(140, "Passenger");
        }

        Assert.True(state.MaskActive);
        Assert.True(state.Pressure >= SenseDefaults.TriggerPressure);
        // FOV radius shrinks continuously with pressure once mask is active (35 → ≥15).
        int tightest = SenseDefaults.TightestNaturalFovRadiusPercent;
        Assert.InRange(state.FovRadiusPercent, tightest, SenseDefaults.DefaultFovRadiusPercent);
    }

    [Fact]
    public void PassengerMode_BuildsPressureFasterThanDriverMode()
    {
        var passenger = new MockInferenceEngine();
        var driver = new MockInferenceEngine();

        SenseState passengerState = passenger.Current;
        SenseState driverState = driver.Current;
        for (int i = 0; i < 5; i++)
        {
            passengerState = passenger.Update(80, "Passenger");
            driverState = driver.Update(80, "Driver");
        }

        Assert.True(passengerState.Pressure > driverState.Pressure);
    }

    // ── OFI attenuation: Driver-mode vs Passenger-mode + mask-active interplay ──
    // Update() applies AT MOST one of two attenuations to the raw sensor OFI:
    //   • Driver mode      => effectiveOfi = raw * 0.25 (passive-flow damping)
    //   • Passenger + mask => effectiveOfi = raw * factor, factor in [0.15, 0.45]
    // The Passenger-mask block is guarded by `!isDriverMode`, so a Driver-mode
    // tick must NEVER stack the mask attenuation on top of the 0.25 factor.
    // SenseState.OpticalFlowIndex carries the post-attenuation effective value,
    // which is what these tests inspect.

    /// <summary>Drive Passenger-mode high OFI until the mask latches on, then
    /// return the engine. Mirrors MockInferenceEngine_ActivatesMaskAboveTriggerPressure.</summary>
    private static SenseState DriveToMaskActive(MockInferenceEngine engine, double ofi = 140)
    {
        SenseState state = engine.Current;
        for (int i = 0; i < 200 && !state.MaskActive; i++)
            state = engine.Update(ofi, "Passenger");
        return state;
    }

    /// <summary>In-memory store that seeds a pre-earned habituation offset so the
    /// composed FOV can sit ABOVE the natural pressure-driven floor (needed to make
    /// the mask-attenuation factor vary off its tight-FOV minimum).</summary>
    private sealed class SeededHabituationStore : IHabituationStore
    {
        private HabituationState? _stored;
        public SeededHabituationStore(double offset) => _stored = new HabituationState { Offset = offset };
        public HabituationState? Load() => _stored;
        public void Save(HabituationState state) => _stored = state;
    }

    // E1
    [Fact]
    public void DriverMode_WithMaskActive_AppliesOnlyDriverAttenuation_NoExtraMaskAttenuation()
    {
        var engine = new MockInferenceEngine();

        // Latch the mask on via Passenger-mode build-up first.
        SenseState state = DriveToMaskActive(engine);
        Assert.True(state.MaskActive); // premise: mask is ON entering the Driver tick

        // One Driver-mode tick with the mask still active. Driver attenuation is a
        // flat 0.25; the Passenger mask-attenuation block must be skipped entirely,
        // so the effective OFI is EXACTLY raw * 0.25 — no double attenuation.
        const double raw = 100.0;
        state = engine.Update(raw, "Driver");

        Assert.Equal(raw * 0.25, state.OpticalFlowIndex, 9); // exact driver factor, no mask stacking
        Assert.Equal(raw, state.RawOpticalFlowIndex, 9);     // raw sensor value preserved unattenuated
    }

    // E2 (a)
    [Fact]
    public void PassengerMode_WithMaskActive_AttenuatesOfiBetween15And45Percent()
    {
        var engine = new MockInferenceEngine();

        DriveToMaskActive(engine);
        Assert.True(engine.Current.MaskActive); // premise: mask is ON entering the probe tick

        // Probe tick in Passenger mode with the mask active. The attenuation factor
        // is clamp(0.45 - tightness*0.30, 0.15, 0.45), so the effective OFI is the
        // raw value scaled by a factor in [0.15, 0.45] — strictly less than raw.
        const double raw = 100.0;
        SenseState state = engine.Update(raw, "Passenger");

        Assert.InRange(state.OpticalFlowIndex, raw * 0.15, raw * 0.45);
        Assert.True(state.OpticalFlowIndex < raw); // attenuation actually fired
        Assert.Equal(raw, state.RawOpticalFlowIndex, 9);
    }

    // E2 (b)
    [Fact]
    public void PassengerMode_WithMaskActive_AttenuationScalesWithFovTightness()
    {
        // TIGHT engine: no earned offset, so a high-pressure mask-active state drives
        // the composed FOV to the natural floor (tightness saturates -> max attenuation).
        var tight = new MockInferenceEngine();

        // WIDE engine: seed a pre-earned habituation offset (Resilient cap) so the same
        // high-pressure mask-active state composes a WIDER FOV (less tightness -> less
        // attenuation). Habituation pauses at startup, so the seeded offset is preserved
        // even under the high-pressure build-up below.
        var wide = new MockInferenceEngine(
            habituationStore: new SeededHabituationStore(SenseDefaults.HabituationMaxOffset));

        DriveToMaskActive(tight);
        DriveToMaskActive(wide);
        Assert.True(tight.Current.MaskActive);
        Assert.True(wide.Current.MaskActive);

        // The attenuation for the probe tick is computed from the FOV STANDING at entry
        // (set by the last build-up tick), so capture it before probing.
        int tightFov = tight.Current.FovRadiusPercent;
        int wideFov  = wide.Current.FovRadiusPercent;
        Assert.True(tightFov < wideFov,
            $"seeded offset should widen the mask-active FOV (tight={tightFov}, wide={wideFov})");

        // Same probe input into both: the tighter FOV must attenuate MORE (smaller
        // effective OFI). This is the monotonic scaling the formula encodes —
        // asserted as an ordering invariant, not a brittle magic number.
        const double raw = 100.0;
        double tightOfi = tight.Update(raw, "Passenger").OpticalFlowIndex;
        double wideOfi  = wide.Update(raw, "Passenger").OpticalFlowIndex;

        Assert.True(tightOfi < wideOfi,
            $"tighter FOV must attenuate more (tightOfi={tightOfi}, wideOfi={wideOfi})");
        // Both still land inside the documented attenuation envelope.
        Assert.InRange(tightOfi, raw * 0.15, raw * 0.45);
        Assert.InRange(wideOfi,  raw * 0.15, raw * 0.45);
    }
}
