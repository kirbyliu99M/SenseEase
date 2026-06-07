using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Backlog C4: <see cref="CalibrationConfidence"/> previously had zero
/// behavioural consumer. The lightest §6-compliant use is a neutral
/// data-quality annotation in the chatbot's <c>[habituation_state]</c>
/// block: when the persisted calibration confidence is
/// <see cref="CalibrationConfidence.Low"/>, the builder emits a single
/// <c>calibration_confidence: low</c> line so the LLM can hedge / probe
/// gently rather than treat the profile as firmly established.
///
/// <para>This is a data-quality signal, NOT a medical claim — High/Medium
/// need no caveat (line omitted) and the never-calibrated case
/// (<c>null</c>) stays silent. These tests pin that exact contract.</para>
/// </summary>
public class CalibrationConfidenceContextTests
{
    private static HabituationProfileSnapshot MinimalSnapshot() => new()
    {
        Profile          = "RESILIENT",
        CurrentVimsTier  = 1,
        HabituationLevel = 0,
        EngineState      = "RUNNING",
    };

    [Fact]
    public void Build_LowConfidence_EmitsDataQualityLine()
    {
        var snap = MinimalSnapshot();
        snap.CalibrationConfidence = CalibrationConfidence.Low;

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.Contains("calibration_confidence: low\n", block);
    }

    [Fact]
    public void Build_HighConfidence_OmitsLine()
    {
        var snap = MinimalSnapshot();
        snap.CalibrationConfidence = CalibrationConfidence.High;

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.DoesNotContain("calibration_confidence", block);
    }

    [Fact]
    public void Build_MediumConfidence_OmitsLine()
    {
        // Only Low warrants a caveat; Medium is treated as good-enough.
        var snap = MinimalSnapshot();
        snap.CalibrationConfidence = CalibrationConfidence.Medium;

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.DoesNotContain("calibration_confidence", block);
    }

    [Fact]
    public void Build_NullConfidence_OmitsLine()
    {
        // Never-calibrated user: CalibrationConfidence is null → no line.
        var snap = MinimalSnapshot();
        snap.CalibrationConfidence = null;

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.DoesNotContain("calibration_confidence", block);
    }
}
