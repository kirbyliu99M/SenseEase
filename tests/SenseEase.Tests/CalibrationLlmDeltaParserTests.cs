using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

public class CalibrationLlmDeltaParserTests
{
    [Fact]
    public void TryParse_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(CalibrationLlmDeltaParser.TryParse(null));
        Assert.Null(CalibrationLlmDeltaParser.TryParse(string.Empty));
        Assert.Null(CalibrationLlmDeltaParser.TryParse("   \n\t  "));
    }

    [Fact]
    public void TryParse_NoDeltaBlock_ReturnsNull()
    {
        const string reply = "Just a plain conversational reply with no JSON block at all.";
        Assert.Null(CalibrationLlmDeltaParser.TryParse(reply));
    }

    [Fact]
    public void TryParse_FullDeltaBlock_ExtractsAllFields()
    {
        const string reply = @"Got it, thanks for sharing.
[CALIBRATION_DELTA]
{
  ""delta"": {
    ""q1_segment"": ""Middle"",
    ""q1_freetext"": ""the spinning camera bit"",
    ""q2_symptoms"": [""Dizziness"", ""Nausea""],
    ""q2_freetext"": null,
    ""q3_intensity"": 3,
    ""q3_freetext"": ""manageable but uncomfortable"",
    ""q4_comparison"": ""Worse"",
    ""q4_no_baseline"": false,
    ""q4_freetext"": null,
    ""q5_risk_factors"": [""MotionSickness""],
    ""q5_freetext"": null
  },
  ""confidence"": [0.8, 0.9, 0.85, 0.7, 0.6],
  ""longitudinal_observation"": ""worse than last time"",
  ""is_complete"": true,
  ""needs_followup_on"": []
}
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Delta);
        Assert.Equal(UncomfortableSegment.Middle, parsed.Delta!.Q1Segment);
        Assert.Equal("the spinning camera bit", parsed.Delta.Q1Freetext);
        Assert.NotNull(parsed.Delta.Q2Symptoms);
        Assert.Equal(2, parsed.Delta.Q2Symptoms!.Count);
        Assert.Contains(SymptomType.Dizziness, parsed.Delta.Q2Symptoms);
        Assert.Contains(SymptomType.Nausea, parsed.Delta.Q2Symptoms);
        Assert.Equal(3, parsed.Delta.Q3Intensity);
        Assert.Equal(BaselineComparison.Worse, parsed.Delta.Q4Comparison);
        Assert.False(parsed.Delta.Q4NoBaseline);
        Assert.NotNull(parsed.Delta.Q5RiskFactors);
        Assert.Single(parsed.Delta.Q5RiskFactors!);
        Assert.Equal(RiskFactor.MotionSickness, parsed.Delta.Q5RiskFactors![0]);
        Assert.True(parsed.IsComplete);
        Assert.Equal("worse than last time", parsed.LongitudinalObservation);
        Assert.NotNull(parsed.Confidence);
        Assert.Equal(5, parsed.Confidence!.Length);
        Assert.Empty(parsed.NeedsFollowupOn);
    }

    [Fact]
    public void TryParse_PartialFields_LeavesUnsetAsNull()
    {
        const string reply = @"[CALIBRATION_DELTA]
{
  ""delta"": {
    ""q3_intensity"": 2
  },
  ""is_complete"": false
}
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Delta);
        Assert.Equal(2, parsed.Delta!.Q3Intensity);
        Assert.Null(parsed.Delta.Q1Segment);
        Assert.Null(parsed.Delta.Q1Freetext);
        Assert.Null(parsed.Delta.Q2Symptoms);
        Assert.Null(parsed.Delta.Q4Comparison);
        Assert.Null(parsed.Delta.Q5RiskFactors);
        Assert.False(parsed.IsComplete);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ this is not valid json at all }
[/CALIBRATION_DELTA]";
        Assert.Null(CalibrationLlmDeltaParser.TryParse(reply));
    }

    [Fact]
    public void TryParse_EmptyBlock_ReturnsNull()
    {
        const string reply = "[CALIBRATION_DELTA][/CALIBRATION_DELTA]";
        Assert.Null(CalibrationLlmDeltaParser.TryParse(reply));
    }

    [Fact]
    public void TryParse_MultipleBlocks_FirstWins()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 1 }, ""is_complete"": false }
[/CALIBRATION_DELTA]
[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 5 }, ""is_complete"": true }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.Delta!.Q3Intensity);
        Assert.False(parsed.IsComplete);
    }

    [Fact]
    public void TryParse_ConfidenceMissing_IsNull()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 4 }, ""is_complete"": true }
[/CALIBRATION_DELTA]";
        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Confidence);
    }

    [Fact]
    public void TryParse_NeedsFollowupOnMissing_IsEmptyArrayNotNull()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 3 }, ""is_complete"": false }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.NeedsFollowupOn);
        Assert.Empty(parsed.NeedsFollowupOn);
    }

    [Fact]
    public void TryParse_NeedsFollowupOnPresent_IsParsed()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": {}, ""is_complete"": false, ""needs_followup_on"": [""q3"", ""q5""] }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.NeedsFollowupOn.Length);
        Assert.Contains("q3", parsed.NeedsFollowupOn);
        Assert.Contains("q5", parsed.NeedsFollowupOn);
    }

    [Fact]
    public void TryParse_UnknownEnumValues_DroppedFromList()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q2_symptoms"": [""Dizziness"", ""Telepathy"", ""Nausea""] }, ""is_complete"": false }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Delta!.Q2Symptoms);
        Assert.Equal(2, parsed.Delta.Q2Symptoms!.Count);
    }

    [Fact]
    public void TryParse_UnknownTopLevelFields_Ignored()
    {
        const string reply = @"[CALIBRATION_DELTA]
{
  ""delta"": { ""q3_intensity"": 2 },
  ""is_complete"": true,
  ""futuristic_new_field"": ""anything goes""
}
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Delta!.Q3Intensity);
    }

    [Fact]
    public void TryParse_OutOfRangeIntensity_IsNulled()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 9 }, ""is_complete"": true }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Delta!.Q3Intensity);
    }

    [Fact]
    public void TryParse_EnumCaseInsensitive()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q1_segment"": ""middle"", ""q4_comparison"": ""WORSE"" }, ""is_complete"": false }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Equal(UncomfortableSegment.Middle, parsed!.Delta!.Q1Segment);
        Assert.Equal(BaselineComparison.Worse, parsed.Delta.Q4Comparison);
    }

    [Fact]
    public void TryParse_SourcesTaggedAsLlm()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 3 }, ""is_complete"": false }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Equal(CalibrationSource.Llm, parsed!.Delta!.Q1Source);
        Assert.Equal(CalibrationSource.Llm, parsed.Delta.Q3Source);
    }

    [Fact]
    public void StripDeltaBlock_RemovesBlock_PreservesText()
    {
        const string reply = @"Got it, thanks for sharing.
[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 3 }, ""is_complete"": false }
[/CALIBRATION_DELTA]
Anything else?";

        string stripped = CalibrationLlmDeltaParser.StripDeltaBlock(reply);
        Assert.DoesNotContain("CALIBRATION_DELTA", stripped);
        Assert.Contains("Got it", stripped);
        Assert.Contains("Anything else?", stripped);
    }

    [Fact]
    public void StripDeltaBlock_NoBlock_ReturnsTrimmedInput()
    {
        const string reply = "just a chat reply";
        Assert.Equal("just a chat reply", CalibrationLlmDeltaParser.StripDeltaBlock(reply));
    }

    [Fact]
    public void StripDeltaBlock_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CalibrationLlmDeltaParser.StripDeltaBlock(null));
    }

    [Fact]
    public void StripDeltaBlock_RemovesAllBlocks()
    {
        const string reply = @"Pre.
[CALIBRATION_DELTA]
{ ""delta"": {}, ""is_complete"": false }
[/CALIBRATION_DELTA]
Middle.
[CALIBRATION_DELTA]
{ ""delta"": {}, ""is_complete"": true }
[/CALIBRATION_DELTA]
Post.";

        string stripped = CalibrationLlmDeltaParser.StripDeltaBlock(reply);
        Assert.DoesNotContain("CALIBRATION_DELTA", stripped);
        Assert.Contains("Pre.", stripped);
        Assert.Contains("Middle.", stripped);
        Assert.Contains("Post.", stripped);
    }

    [Fact]
    public void TryParse_BlankFreetext_IsNulled()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 3, ""q1_freetext"": ""   "" }, ""is_complete"": false }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Delta!.Q1Freetext);
    }

    [Fact]
    public void TryParse_Q4NoBaselineTrue_IsPreserved()
    {
        const string reply = @"[CALIBRATION_DELTA]
{ ""delta"": { ""q3_intensity"": 1, ""q4_no_baseline"": true }, ""is_complete"": true }
[/CALIBRATION_DELTA]";

        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        Assert.NotNull(parsed);
        Assert.True(parsed!.Delta!.Q4NoBaseline);
    }
}
