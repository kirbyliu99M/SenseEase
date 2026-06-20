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
[Collection("ChatbotUiLanguage")] // TryBuildFollowup → PreferZh reads the UI-language pin — serialize
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

    // ── 2026-06-20 clarification-card taxonomy: ask a MORE RELEVANT card than "which symptom?" ──

    [Theory]
    // Card B — a vague CHANGE request (no symptom, no direction) → "what should I adjust?", NOT the symptom picker.
    [InlineData("can you make it better")]
    [InlineData("just fix it")]
    [InlineData("幫我弄一下")]
    [InlineData("調整一下")]
    public void VagueChangeRequest_BuildsWhatToAdjustCard(string text)
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup(text, "Unknown", 1);
        Assert.NotNull(q);
        Assert.NotNull(opts);
        Assert.InRange(opts!.Length, 2, 4);
        Assert.Contains(opts, o => o.Contains("motion") || o.Contains("動態") || o.Contains("Steadier") || o.Contains("穩定"));
        Assert.DoesNotContain(opts, o => o.Contains("Dizziness") || o.Contains("暈眩"));   // not a symptom card
    }

    [Theory]
    // Card D — an edge/brightness complaint with no direction → "darker or lighter?".
    [InlineData("邊緣怪怪的")]
    [InlineData("the edges look off to me")]
    public void EdgeComplaintNoDirection_BuildsDarkerOrLighterCard(string text)
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup(text, "Unknown", 1);
        Assert.NotNull(q);
        Assert.NotNull(opts);
        Assert.Equal(2, opts!.Length);
        Assert.Contains(opts, o => o.Contains("Darker") || o.Contains("更暗"));
        Assert.Contains(opts, o => o.Contains("Lighter") || o.Contains("更亮"));
    }

    [Theory]
    // Card E — two+ conflicting symptoms named → "which is bothering you most?" (the symptom chips, new question).
    [InlineData("dizzy and my eyes hurt")]
    [InlineData("又暈又眼睛痛")]
    public void TwoConflictingSymptoms_AskWhichIsWorst(string text)
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup(text, "ReportDiscomfort", 2);
        Assert.NotNull(q);
        Assert.True(q!.Contains("bothering") || q.Contains("困擾"), "card E asks which is worst");
        Assert.NotNull(opts);
        Assert.InRange(opts!.Length, 2, 4);
    }

    [Fact]
    // The "Steadier view" chip must route to a STRONGER anchor grid on the keyword-fallback path.
    public void SteadierViewChip_RoutesToStrongerGrid()
    {
        var (_, gvZh, _, _) = FeedbackLexicon.ClassifyShaderHints("穩定畫面");
        Assert.True(gvZh > 0, "steadier-view (zh) should strengthen the anchor grid");
        var (_, gvEn, _, _) = FeedbackLexicon.ClassifyShaderHints("steadier view");
        Assert.True(gvEn > 0, "steadier-view (en) should strengthen the anchor grid");
    }
}
