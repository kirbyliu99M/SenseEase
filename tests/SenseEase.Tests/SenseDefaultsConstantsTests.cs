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

    // ── D-4 (LOGIC-05/NE9, ratified 2026-06-11): cursor-move OFI attenuation ──
    // Separate knob from DriverFlowAttenuation by design — the equal magnitude
    // is intentional today but the two semantics may diverge later.
    [Fact]
    public void CursorMoveOfiAttenuation_equals_ratified_value()
        => Assert.Equal(0.25, SenseDefaults.CursorMoveOfiAttenuation);

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

    // ── TEST-10: locked-constant literal pins ─────────────────────────────────
    // Each assert pins the constant to the exact literal value used in the shipped
    // engine. Any accidental value change breaks this test before behaviour tests
    // surface the drift.  Lock source noted inline per HANDOVER.md / whitepaper §12.

    /// <summary>Lock source: HANDOVER.md hard-constraint — engine trigger threshold.</summary>
    [Fact]
    public void TriggerPressure_locked_at_85()
        => Assert.Equal(85.0, SenseDefaults.TriggerPressure);

    /// <summary>Lock source: whitepaper §12 — OFI noise floor; below this OFI is zeroed.</summary>
    [Fact]
    public void NoiseGateThreshold_locked_at_8()
        => Assert.Equal(8.0, SenseDefaults.NoiseGateThreshold);

    /// <summary>Lock source: whitepaper §12 — passive-flow integrator multiplier.</summary>
    [Fact]
    public void PassiveFlowWeight_locked_at_2()
        => Assert.Equal(2.0, SenseDefaults.PassiveFlowWeight);

    /// <summary>Lock source: whitepaper §12 — WGC MAD-to-OFI normalisation factor.</summary>
    [Fact]
    public void WgcMadNormFactor_locked_at_4()
        => Assert.Equal(4.0, SenseDefaults.WgcMadNormFactor);

    /// <summary>Lock source: HANDOVER.md hard-constraint — cursor + WGC EMA smoothing alpha.</summary>
    [Fact]
    public void CursorEmaAlpha_locked_at_0_55()
        => Assert.Equal(0.55, SenseDefaults.CursorEmaAlpha);

    /// <summary>Lock source: whitepaper §12 — overlay brush baseline radius
    /// (RelativeToBoundingBox); inactive mask snaps to this value.</summary>
    [Fact]
    public void OverlayBrushRadius_locked_at_0_85()
        => Assert.Equal(0.85, SenseDefaults.OverlayBrushRadius);
}
