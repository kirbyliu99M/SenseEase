using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Lexicon backstop (Kirby 2026-06-22): a few short ASCII keywords ("ok", "okay", "fine", "tired",
/// "so", "very", "ar", "vr", "des") used to substring-match FALSELY inside common words ("ok" in
/// "look"/"took", "fine" in "define", "tired" in "retired", "very" in "every", "ar" in "car"). They now
/// require a WHOLE-TOKEN match. These pin BOTH the fix (no false match) AND that genuine uses plus the
/// intentional stems ("dizz" → dizzy/dizziness, "blur" → blurry) keep matching on substring as before.
/// </summary>
public class FeedbackLexiconBoundaryTests
{
    [Theory]
    [InlineData("I took a break")]        // "ok" inside "took"
    [InlineData("let me book the room")]  // "ok" inside "book"
    public void EmbeddedOk_DoesNotClassifyComfort(string text)
        => Assert.NotEqual("ReportComfort", FeedbackLexicon.Classify(text).intent);

    [Theory]
    [InlineData("let me define the problem")] // "fine" inside "define"
    [InlineData("we should refine this")]     // "fine" inside "refine"
    public void EmbeddedFine_IsNotComfortOrNegation(string text)
        => Assert.False(FeedbackLexicon.ExpressesComfortOrNegation(text));

    [Fact]
    public void EmbeddedTired_DoesNotClassifyDiscomfort()
        => Assert.NotEqual("ReportDiscomfort", FeedbackLexicon.Classify("I retired last year").intent);

    [Fact]
    public void EmbeddedAr_IsNotAContextTrigger()
        => Assert.False(FeedbackLexicon.HasContextTrigger("my car broke down"));

    // ── genuine uses of the same words still match ──
    [Theory]
    [InlineData("I'm ok now")]
    [InlineData("I feel fine")]
    [InlineData("okay, all good")]
    public void GenuineComfortWords_StillClassifyComfort(string text)
        => Assert.Equal("ReportComfort", FeedbackLexicon.Classify(text).intent);

    [Fact]
    public void GenuineTired_StillDiscomfort()
        => Assert.Equal("ReportDiscomfort", FeedbackLexicon.Classify("I'm so tired").intent);

    [Fact]
    public void GenuineAr_StillContextTrigger()
        => Assert.True(FeedbackLexicon.HasContextTrigger("I use AR a lot"));

    [Fact]
    public void So_StillCountsAsSeverityAdverb()
        => Assert.True(FeedbackLexicon.SeverityScore("I'm so dizzy") >= 0.75);

    // ── intentional stems are NOT word-boundary-gated (still substring) ──
    [Theory]
    [InlineData("I'm dizzy")]                 // "dizz"
    [InlineData("dizziness is getting worse")] // "dizz"
    [InlineData("everything looks blurry")]    // "blur"
    public void Stems_StillDetectSpecificSymptom(string text)
        => Assert.True(FeedbackLexicon.MentionsSpecificSymptom(text));
}
