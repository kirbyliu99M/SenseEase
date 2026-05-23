using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

public class FollowupLexiconTests
{
    [Fact]
    public void Vague_English_Input_With_Unknown_Intent_Asks_For_Symptom()
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup("I feel off", "Unknown", 1);
        Assert.NotNull(q);
        Assert.NotNull(opts);
        Assert.InRange(opts!.Length, 2, 4);
        Assert.Contains(opts, o => o.Contains("Dizziness"));
    }

    [Fact]
    public void Vague_Chinese_Input_Asks_In_Chinese()
    {
        var (q, _) = FeedbackLexicon.TryBuildFollowup("怪怪的", "Unknown", 1);
        Assert.NotNull(q);
        Assert.Contains("不適", q!);
    }

    [Fact]
    public void Specific_Symptom_Mention_Suppresses_Followup()
    {
        // User already named "dizzy" — bot should not re-ask which symptom.
        var (q, opts) = FeedbackLexicon.TryBuildFollowup("I feel dizzy", "ReportDiscomfort", 4);
        Assert.Null(q);
        Assert.Null(opts);
    }

    [Fact]
    public void Bare_Word_Dizziness_Suppresses_Followup()
    {
        // Regression: the "dizzi" stem must match both "dizzy" and "dizziness".
        // Earlier version used "dizzy" which fails to match "dizziness" as substring
        // ('y' vs 'i' at position 4), causing a followup loop when the user tapped
        // the Dizziness option card.
        var (q, _) = FeedbackLexicon.TryBuildFollowup("Dizziness", "ReportDiscomfort", 3);
        Assert.Null(q);
    }

    [Fact]
    public void Mild_Unspecific_Discomfort_Now_Triggers_Followup()
    {
        // Policy change (per user feedback): even mild "I'm uncomfortable" with no
        // specific symptom must clarify before applying any FOV delta. The earlier
        // severity>=3 gate let small unspecified reports silently apply a default
        // adjustment, which surprised users.
        var (q, opts) = FeedbackLexicon.TryBuildFollowup("I'm uncomfortable", "ReportDiscomfort", 1);
        Assert.NotNull(q);
        Assert.NotNull(opts);
    }

    [Fact]
    public void Unknown_NonVague_Input_Also_Asks()
    {
        // Off-topic / unknown chatter ("how's the weather") should still prompt the
        // open symptom picker — the user might still want help, just hasn't said so.
        var (q, opts) = FeedbackLexicon.TryBuildFollowup("how's the weather", "Unknown", 1);
        Assert.NotNull(q);
        Assert.NotNull(opts);
    }

    [Fact]
    public void Severe_NonSpecific_Discomfort_Asks_Which_Symptom_Strongest()
    {
        // Severity ≥ 3 + no specific symptom → confirm before applying heavy deltas.
        var (q, opts) = FeedbackLexicon.TryBuildFollowup("I feel really uncomfortable", "ReportDiscomfort", 4);
        Assert.NotNull(q);
        Assert.NotNull(opts);
        Assert.Equal(4, opts!.Length);
    }

    [Fact]
    public void Empty_Input_Returns_No_Followup()
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup(string.Empty, "Unknown", 1);
        Assert.Null(q);
        Assert.Null(opts);
    }

    [Fact]
    public void Spike_Followup_Returns_Question_And_Four_Options()
    {
        var (q, opts) = FeedbackLexicon.BuildSpikeFollowup(isChineseUiHint: false, spikeNormalized: 0.5);
        Assert.False(string.IsNullOrWhiteSpace(q));
        Assert.NotNull(opts);
        Assert.Equal(4, opts.Length);
        Assert.Contains(opts, o => o.Contains("Dizziness"));
    }

    [Theory]
    [InlineData(0.1, "small")]
    [InlineData(0.4, "noticeable")]
    [InlineData(0.8, "sharp")]
    public void Spike_Followup_Intensity_Wording_Tracks_Magnitude(double mag, string expectedFragment)
    {
        var (q, _) = FeedbackLexicon.BuildSpikeFollowup(isChineseUiHint: false, spikeNormalized: mag);
        Assert.Contains(expectedFragment, q);
    }

    // Round-trip check: when the user picks a specific symptom card the lexicon
    // must NOT recurse into another followup. This was a real bug-hunt scenario
    // during Phase 3 smoke testing.
    [Theory]
    [InlineData("Dizziness")]
    [InlineData("Nausea")]
    [InlineData("Headache")]
    [InlineData("Eye strain")]
    public void Picking_A_Symptom_Card_Does_Not_Trigger_Another_Followup(string option)
    {
        // Mirror what BuildFallbackResult does: classify, pick severity, then probe followup.
        var (intent, _) = FeedbackLexicon.Classify(option);
        int sev = FeedbackLexicon.SeverityBucket(option);
        var (q, opts) = FeedbackLexicon.TryBuildFollowup(option, intent, sev);
        Assert.Equal("ReportDiscomfort", intent);
        Assert.True(q is null && opts is null,
            $"Expected no followup for '{option}'. Got: intent={intent}, sev={sev}, q={q ?? "<null>"}");
    }
}
