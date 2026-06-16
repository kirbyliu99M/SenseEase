using System;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 D-18/D-19: pins the 5★ comfort rating ↔ FMS 0-20 mapping and the measured-report
/// builder. The mapping is the clinical contract behind the honest ON-vs-OFF comfort delta that
/// replaces the retired "70%" claim, so it is locked: inverse (more stars ⇒ less discomfort),
/// evenly spaced, full-range, and the built event is unambiguously a MEASURED PRO.
/// </summary>
public sealed class ComfortRatingScaleTests
{
    [Theory]
    [InlineData(5, 0)]   // very comfortable → no symptoms
    [InlineData(4, 5)]
    [InlineData(3, 10)]
    [InlineData(2, 15)]
    [InlineData(1, 20)]  // very uncomfortable → severe
    public void StarsToFms_MapsEachStar_InverselyAndEvenlySpaced(int stars, int expectedFms)
        => Assert.Equal(expectedFms, ComfortRatingScale.StarsToFms(stars));

    [Fact]
    public void StarsToFms_IsStrictlyDecreasingInStars()
    {
        // More comfort ⇒ strictly lower discomfort. Guards against an accidental sign flip.
        int prev = int.MaxValue;
        for (int s = ComfortRatingScale.MinStars; s <= ComfortRatingScale.MaxStars; s++)
        {
            int fms = ComfortRatingScale.StarsToFms(s);
            Assert.True(fms < prev, $"FMS must strictly decrease as stars rise (star {s} → {fms}, prev {prev})");
            prev = fms;
        }
    }

    [Fact]
    public void StarsToFms_SpansTheFullFmsRange()
    {
        Assert.Equal(ComfortRatingScale.FmsMin, ComfortRatingScale.StarsToFms(ComfortRatingScale.MaxStars));
        Assert.Equal(ComfortRatingScale.FmsMax, ComfortRatingScale.StarsToFms(ComfortRatingScale.MinStars));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void StarsToFms_OutOfRange_Throws(int stars)
        // Fail loud — a miswired caller must not silently record a garbage clinical PRO.
        => Assert.Throws<ArgumentOutOfRangeException>(() => ComfortRatingScale.StarsToFms(stars));

    [Fact]
    public void BuildMeasuredReport_IsMeasuredFmsReport_FromStars()
    {
        var ev = ComfortRatingScale.BuildMeasuredReport(
            stars: 2, contextTier: 7, maskActive: true, foregroundKey: "category:video", trigger: "");

        Assert.True(ev.Measured);                                   // a PRO, never an estimate
        Assert.Equal(SymptomEvent.Kinds.FmsReport, ev.Kind);
        Assert.Equal(SymptomEvent.Scales.Fms20, ev.Scale);
        Assert.Equal(SymptomEvent.Sources.Stars, ev.Source);
        Assert.Equal(15, ev.Value);                                 // 2★ → FMS 15
        Assert.Equal(7, ev.ContextTier);
        Assert.Equal(SymptomEvent.InterventionStates.On, ev.InterventionState);
        Assert.Equal("category:video", ev.ForegroundKey);
        Assert.Equal(SymptomEvent.Triggers.Manual, ev.Trigger);    // blank → manual
        Assert.True(string.IsNullOrEmpty(ev.TsUtc));               // store stamps at append
        Assert.False(ev.Exportable);                               // raw rows never export (D-17)
    }

    [Fact]
    public void BuildMeasuredReport_MaskOff_RecordsInterventionOff()
    {
        var ev = ComfortRatingScale.BuildMeasuredReport(
            stars: 5, contextTier: 1, maskActive: false, foregroundKey: null, trigger: "session_end");

        Assert.Equal(SymptomEvent.InterventionStates.Off, ev.InterventionState);
        Assert.Null(ev.ForegroundKey);                             // null when consent off
        Assert.Equal("session_end", ev.Trigger);                  // explicit trigger preserved
        Assert.Equal(0, ev.Value);                                 // 5★ → FMS 0
    }

    // ── D-redesign: the 0-20 slider/band capture records the FULL FMS resolution ──

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(20)]
    public void BuildMeasuredFmsReport_RecordsTheRawFmsValue_AsAMeasuredSliderPro(int fms)
    {
        var ev = ComfortRatingScale.BuildMeasuredFmsReport(
            fms, contextTier: 4, maskActive: true, foregroundKey: null, trigger: "");

        Assert.True(ev.Measured);                                   // a PRO, never an estimate
        Assert.Equal(SymptomEvent.Kinds.FmsReport, ev.Kind);
        Assert.Equal(SymptomEvent.Scales.Fms20, ev.Scale);
        Assert.Equal(SymptomEvent.Sources.Slider, ev.Source);      // the 0-20 capture, not stars
        Assert.Equal(fms, ev.Value);                               // full 0-20 resolution preserved
        Assert.Equal(4, ev.ContextTier);
        Assert.Equal(SymptomEvent.InterventionStates.On, ev.InterventionState);
        Assert.Equal(SymptomEvent.Triggers.Manual, ev.Trigger);    // blank → manual
        Assert.False(ev.Exportable);                               // raw rows never export (D-17)
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    [InlineData(100)]
    public void BuildMeasuredFmsReport_OutOfRange_Throws(int fms)
        // Fail loud — a miswired slider must not record a garbage clinical PRO.
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            ComfortRatingScale.BuildMeasuredFmsReport(fms, 1, true, null, ""));

    [Fact]
    public void BuildMeasuredFmsReport_MaskOff_PreservesTriggerAndInterventionOff()
    {
        var ev = ComfortRatingScale.BuildMeasuredFmsReport(
            fms: 13, contextTier: 9, maskActive: false, foregroundKey: "category:video", trigger: "spike");

        Assert.Equal(SymptomEvent.InterventionStates.Off, ev.InterventionState);
        Assert.Equal("category:video", ev.ForegroundKey);
        Assert.Equal("spike", ev.Trigger);                         // explicit trigger preserved
        Assert.Equal(13, ev.Value);
    }
}
