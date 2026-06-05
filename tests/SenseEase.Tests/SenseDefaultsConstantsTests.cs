using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Readability-refactor guard rails. The Alpha-1 constants sprint
/// replaced a handful of magic-number literals with named constants while
/// keeping every VALUE byte-identical (the behaviour suite pins the engine).
///
/// These tests pin each NEW <see cref="SenseDefaults"/> constant to the exact
/// literal it replaced, and assert the mirrored class-level constants still
/// resolve to the same single source of truth. If a future edit accidentally
/// nudges one of these numbers, a test here fails immediately — long before
/// the larger behaviour suite would surface a subtle drift.</summary>
public class SenseDefaultsConstantsTests
{
    // ── D2: driver-mode passive-flow attenuation ──────────────────────────────
    [Fact]
    public void DriverFlowAttenuation_equals_replaced_literal()
        => Assert.Equal(0.25, SenseDefaults.DriverFlowAttenuation);

    // ── F1: personal_pressure_scale clamp bounds (calibration §7) ─────────────
    [Fact]
    public void PersonalPressureScaleMin_equals_replaced_literal()
        => Assert.Equal(0.7, SenseDefaults.PersonalPressureScaleMin);

    [Fact]
    public void PersonalPressureScaleMax_equals_replaced_literal()
        => Assert.Equal(1.5, SenseDefaults.PersonalPressureScaleMax);

    // The two processors mirror the canonical pair — pin them in sync so a
    // future divergence between the public surfaces is caught.
    [Fact]
    public void CalibrationProcessor_mirrors_canonical_scale_bounds()
    {
        Assert.Equal(SenseDefaults.PersonalPressureScaleMin, CalibrationProcessor.PersonalPressureScaleMin);
        Assert.Equal(SenseDefaults.PersonalPressureScaleMax, CalibrationProcessor.PersonalPressureScaleMax);
    }

    [Fact]
    public void HabituationOverrideProcessor_mirrors_canonical_scale_bounds()
    {
        Assert.Equal(SenseDefaults.PersonalPressureScaleMin, HabituationOverrideProcessor.PersonalPressureScaleMin);
        Assert.Equal(SenseDefaults.PersonalPressureScaleMax, HabituationOverrideProcessor.PersonalPressureScaleMax);
    }

    // ── C1-vest: vestibular direct trigger-pressure override ──────────────────
    [Fact]
    public void VestibularTriggerPressureOverride_equals_replaced_literal()
        => Assert.Equal(50, CalibrationProcessor.VestibularTriggerPressureOverride);
}
