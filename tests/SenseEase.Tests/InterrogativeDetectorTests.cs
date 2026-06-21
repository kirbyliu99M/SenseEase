using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// The deterministic "asking-about-state vs stating-it" guard behind the chatbot actuation chokepoint.
/// Origin: a tester asked "do you think I'm dizzy now?" and the 3B model read it as "I feel dizzy now"
/// and adjusted the overlay unsolicited. The detector must flag the asking-the-bot class so the guard can
/// suppress it — while NEVER flagging a genuine report, even a help-seeking one that ends with a question
/// ("I'm dizzy, can you help?"), or that report would be wrongly suppressed.
/// </summary>
public class InterrogativeDetectorTests
{
    [Theory]
    // The tester's exact case (+ a punctuation-less variant) and its Chinese equivalents — the bot is
    // being asked to ASSESS the user's state.
    [InlineData("do you think I'm dizzy now?")]
    [InlineData("do you think I'm dizzy now")]
    [InlineData("你覺得我現在會頭暈嗎?")]
    [InlineData("你覺得我頭暈")]
    [InlineData("是不是頭暈了")]
    [InlineData("am I dizzy?")]
    [InlineData("am I going to get sick")]
    [InlineData("can you tell if I'm nauseous?")]
    [InlineData("do I look unwell?")]
    public void AskingAboutState_IsDetected(string text)
        => Assert.True(InterrogativeDetector.IsSymptomInquiry(text));

    [Theory]
    // First-person STATEMENTS, commands, and — critically — HELP-SEEKING REPORTS that end with a
    // question. These assert a symptom (or a command) and must NOT be flagged, so they still actuate.
    [InlineData("I'm dizzy")]
    [InlineData("I feel dizzy now")]
    [InlineData("頭暈")]
    [InlineData("我覺得有點頭暈")]                  // "I feel a bit dizzy" — 我覺得, NOT 你覺得
    [InlineData("我想吐")]
    [InlineData("眼睛好痛")]
    [InlineData("I'm dizzy, can you help?")]       // report + help-question — must still actuate
    [InlineData("我頭暈，可以幫我嗎？")]            // Chinese report + help-question — must still actuate
    [InlineData("why am I so dizzy?!")]            // a report venting (wh + assertion), not an inquiry
    [InlineData("make it darker")]
    [InlineData("reset everything")]
    [InlineData("what a headache")]
    [InlineData("")]
    [InlineData("   ")]
    public void StatementsCommandsAndHelpSeekingReports_AreNotFlagged(string text)
        => Assert.False(InterrogativeDetector.IsSymptomInquiry(text));

    [Fact]
    public void Null_IsNotFlagged()
        => Assert.False(InterrogativeDetector.IsSymptomInquiry(null));

    [Theory]
    // "Should I take this action?" deliberation — must NOT fire a destructive intent (a reset).
    [InlineData("should I reset everything?")]
    [InlineData("Should I reset to default")]
    [InlineData("do I need to reset?")]
    [InlineData("要不要重設？")]
    [InlineData("該不該重設")]
    [InlineData("需要重設嗎？")]
    public void DeliberationQuestion_IsCommandInquiry(string text)
        => Assert.True(InterrogativeDetector.IsCommandInquiry(text));

    [Theory]
    // Plain imperatives / statements — a real command, never a deliberation question.
    [InlineData("reset everything to default")]
    [InlineData("reset")]
    [InlineData("I'm dizzy")]
    [InlineData("重設所有設定")]
    [InlineData("make it darker")]
    [InlineData("")]
    public void Imperatives_AreNotCommandInquiry(string text)
        => Assert.False(InterrogativeDetector.IsCommandInquiry(text));

    [Theory]
    // A self-report carrying an UNCERTAINTY marker — the user names a symptom but hedges it, so the gate
    // should ASK to confirm (the caller pairs this with a specific-symptom check).
    [InlineData("I feel dizzy? I don't know")]   // the tester's exact case
    [InlineData("maybe I'm a bit nauseous")]
    [InlineData("I'm not sure, kind of a headache")]
    [InlineData("could be eye strain")]
    [InlineData("好像有點暈，不太確定")]
    [InlineData("可能有點噁心")]
    [InlineData("我不知道是不是頭痛")]
    public void HedgedSelfReport_IsDetected(string text)
        => Assert.True(InterrogativeDetector.IsHedgedSymptom(text));

    [Theory]
    // Confident reports + help-seeking requests with NO hedge marker — must NOT be flagged, so they still
    // actuate (a hedge marker, not a bare "?", is what distinguishes uncertainty).
    [InlineData("I'm dizzy")]
    [InlineData("I feel dizzy now")]
    [InlineData("I'm dizzy, can you help?")]
    [InlineData("頭暈")]
    [InlineData("我頭暈，可以幫我嗎？")]
    [InlineData("make it darker")]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfidentReport_IsNotHedged(string text)
        => Assert.False(InterrogativeDetector.IsHedgedSymptom(text));

    [Fact]
    public void Null_IsNotHedged() => Assert.False(InterrogativeDetector.IsHedgedSymptom(null));
}
