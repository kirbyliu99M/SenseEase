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
/// changing the engine tier.
///
/// <para>D-13: the personalized trigger is now set through the LIVE chatbot path
/// (<c>ApplyChatbotOverride(AdjustSensitivity, scale)</c> → <c>EffectiveTrigger = 85/scale</c>,
/// scale clamped to [0.7,1.5]) rather than the deleted calibration plumbing. A higher scale
/// (more sensitive) LOWERS the trigger; a lower scale (more resilient) RAISES it. The exact
/// 50/130 trigger literals the old override-result used are outside the scale-derived range,
/// but this test only needs SENSITIVE's trigger &lt; RESILIENT's, which scale 1.5 (≈56.67) vs
/// 0.7 (≈121.43) preserves.</para></summary>
public class DisplayStressTests
{
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
        sensitive.ApplyChatbotOverride(OverrideIntent.AdjustSensitivity, 1.5); // scale 1.5 → trigger ≈56.67 (more sensitive)
        var resilient = new MockInferenceEngine();
        resilient.ApplyChatbotOverride(OverrideIntent.AdjustSensitivity, 0.7); // scale 0.7 → trigger ≈121.43 (more resilient)
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
