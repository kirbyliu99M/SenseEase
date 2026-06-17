using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Tester-reported gap (2026-06-17): "I don't know, but I feel weird with my eyes" silently cut the FOV
/// instead of asking. Root cause — the EyeMarkers set contained the bare location word "eye", so a vague
/// sensation near a body part counted as a SPECIFIC symptom and skipped the clarify picker. These pin the
/// fix at the lexicon level: a bare location is NOT a specific symptom (→ clarify), while a real eye
/// symptom still is (→ act).
/// </summary>
public class FeedbackLexiconClarifyTests
{
    [Theory]
    // Vague unease near a location, or generic unease — NOT a specific symptom ⇒ should clarify.
    [InlineData("I feel weird with my eyes")]
    [InlineData("I don't know, but I feel weird with my eyes")]
    [InlineData("my eyes feel strange")]
    [InlineData("I feel off")]
    [InlineData("something is wrong")]
    [InlineData("怪怪的")]
    [InlineData("我不太舒服")]
    public void NoSpecificSymptom_IsNotFlaggedSpecific(string text)
        => Assert.False(FeedbackLexicon.MentionsSpecificSymptom(text));

    [Theory]
    // Real, specific symptoms — must STILL count as specific ⇒ act, don't over-ask.
    [InlineData("I have eye strain")]
    [InlineData("my eyes hurt")]
    [InlineData("dry eyes")]
    [InlineData("everything is blurry")]
    [InlineData("眼睛痛")]
    [InlineData("眼睛酸")]
    [InlineData("I'm dizzy")]
    [InlineData("頭暈")]
    [InlineData("I have nausea")]
    public void SpecificSymptom_IsFlaggedSpecific(string text)
        => Assert.True(FeedbackLexicon.MentionsSpecificSymptom(text));

    [Theory]
    // ReportDiscomfort with NO specific symptom ⇒ the clarify picker fires (non-null question + options).
    [InlineData("I feel weird with my eyes")]
    [InlineData("I don't know, but I feel weird with my eyes")]
    [InlineData("I feel off")]
    public void Discomfort_NoSpecificSymptom_BuildsClarifyPicker(string text)
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup(text, "ReportDiscomfort", 2);
        Assert.False(string.IsNullOrWhiteSpace(q));
        Assert.NotNull(opts);
        Assert.InRange(opts!.Length, 2, 4);
    }

    [Theory]
    // ReportDiscomfort WITH a specific symptom ⇒ no picker (act directly, don't over-ask).
    [InlineData("I have eye strain")]
    [InlineData("眼睛痛")]
    [InlineData("I'm dizzy")]
    [InlineData("I have a headache")]
    public void Discomfort_SpecificSymptom_NoPicker(string text)
    {
        var (q, _) = FeedbackLexicon.TryBuildFollowup(text, "ReportDiscomfort", 2);
        Assert.Null(q);
    }
}
