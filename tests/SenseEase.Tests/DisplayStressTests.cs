using System;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>P1 (personalization-visible): the displayed stress is a percentage of
/// THIS user's personalized effective trigger, so the SAME raw pressure reads as a
/// DIFFERENT DisplayStressPercent for a SENSITIVE (low trigger) vs a RESILIENT (high
/// trigger) user — closing the "personalization is invisible in the hero" gap WITHOUT
/// changing the engine tier.</summary>
public class DisplayStressTests
{
    private static CalibrationResult TriggerOverride(int overrideValue) => new()
    {
        Profile                    = HabituationProfile.Typical,
        PersonalPressureScale      = 1.0,
        TriggerThresholdOverride   = overrideValue,
        TriggerThresholdAdjustment = 0,
        SevereStrikeLimitOverride  = null,
        MedicalAdvisory            = null,
        SymptomDominancePrimary    = null,
        CalibrationConfidence      = CalibrationConfidence.High,
        Completeness               = CalibrationCompleteness.Complete,
    };

    [Fact]
    public void DisplayStressPercent_TracksPressureOverEffectiveTrigger()
    {
        var engine = new MockInferenceEngine(); // default trigger = 85
        for (int i = 0; i < 40; i++) engine.Update(20.0, "Passenger");

        double p = engine.Current.Pressure;
        int expected = (int)Math.Round(Math.Clamp(p / SenseDefaults.TriggerPressure * 100.0, 0, 100));
        Assert.Equal(expected, engine.Current.DisplayStressPercent);
    }

    [Fact]
    public void DisplayStressPercent_PersonalizationVisible_SensitiveExceedsResilient()
    {
        var sensitive = new MockInferenceEngine();
        sensitive.ApplyCalibrationResult(TriggerOverride(50));   // lower trigger = more sensitive
        var resilient = new MockInferenceEngine();
        resilient.ApplyCalibrationResult(TriggerOverride(130));  // higher trigger = more resilient
        Assert.True(sensitive.HabituationEffectiveTrigger < resilient.HabituationEffectiveTrigger);

        // Drive IDENTICAL low-OFI input until raw pressure reaches ~25 — safely below
        // BOTH triggers, so the mask stays inactive in both and the leaky integrator
        // accumulates identically (no attenuation divergence). Both pressures equal.
        for (int i = 0; i < 80 && sensitive.Current.Pressure < 25.0; i++)
        {
            sensitive.Update(20.0, "Passenger");
            resilient.Update(20.0, "Passenger");
        }

        Assert.False(sensitive.Current.MaskActive);                              // mask inactive in both
        Assert.False(resilient.Current.MaskActive);
        Assert.Equal(sensitive.Current.Pressure, resilient.Current.Pressure, 1); // same raw pressure
        Assert.True(sensitive.Current.Pressure > 0);                            // meaningful pressure

        Assert.True(
            sensitive.Current.DisplayStressPercent > resilient.Current.DisplayStressPercent,
            $"SENSITIVE {sensitive.Current.DisplayStressPercent}% should exceed RESILIENT " +
            $"{resilient.Current.DisplayStressPercent}% at equal pressure {sensitive.Current.Pressure:F1}");
    }
}
