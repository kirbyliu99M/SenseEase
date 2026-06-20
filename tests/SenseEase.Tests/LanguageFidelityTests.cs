using System.Linq;
using SenseEase.Infrastructure.Nlp;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Language-fidelity regression (UVHS 2026-06-21): "app set to English, the chatbot still replied in
/// Chinese." The decision side (the pin) was correct; the leak was that nothing on the OUTPUT side
/// enforced the pinned language — a temperature-0.4 China-trained model drifts past the prompt pin, and
/// the lexicon fallbacks picked language from the user's INPUT SCRIPT rather than the UI toggle.
///
/// <para>These pin the now-deterministic guarantee: when the UI language is known, a reply whose script
/// contradicts it is swapped wholesale to the pinned language (<see cref="ReplyComplianceScrubber"/>
/// backstop), and the lexicon fallbacks follow the pin (<see cref="FeedbackLexicon"/>). They pass the pin
/// EXPLICITLY (the <c>uiEnglishPin</c> argument) rather than mutating the process-global
/// <c>ChatbotUiLanguage</c> — production reads that global live, but a test that flipped it would race
/// other suites (the global is also wired, and never restored, by MainViewModel construction).</para>
/// </summary>
public class LanguageFidelityTests
{
    private static bool HasCjk(string? s)
        => !string.IsNullOrEmpty(s) && s!.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    // ── ReplyComplianceScrubber output-side language backstop (the primary fix) ───────────────

    [Fact]
    public void Scrub_EnPinned_CleanChineseReply_SwappedToEnglish()
    {
        // The exact failure shape: a clean Chinese sentence that trips none of the tone/claim filters
        // and used to ship verbatim under an English UI.
        string s = ReplyComplianceScrubber.Scrub("你好，目前一切穩定。", "ReportComfort", actuated: false, "hi", uiEnglishPin: true);
        Assert.False(HasCjk(s), $"EN-pinned reply still contained Han: {s}");
        Assert.NotEqual(0, s.Length);
    }

    [Fact]
    public void Scrub_EnPinned_ChineseActionClaim_NotActuated_StaysHonestAndEnglish()
    {
        // A Chinese reply that ALSO falsely claims an action while nothing actuated: must end up English
        // AND must not claim a change (R1 honesty preserved through the swap).
        string s = ReplyComplianceScrubber.Scrub("已將畫面晃動降到最低。", "ReportDiscomfort", actuated: false, "make it better", uiEnglishPin: true);
        Assert.False(HasCjk(s));
        Assert.DoesNotContain("i've", s.ToLowerInvariant()); // no fabricated "I've changed X"
        Assert.DoesNotContain("reduced", s.ToLowerInvariant());
    }

    [Fact]
    public void Scrub_EnPinned_AnalyzeChineseReply_SwappedToEnglish()
    {
        string s = ReplyComplianceScrubber.Scrub("畫面動態是主要原因。", "AnalyzeRequest", actuated: false, "why?", uiEnglishPin: true);
        Assert.False(HasCjk(s));
    }

    [Fact]
    public void Scrub_EnPinned_EnglishReply_PassesThroughUnchanged()
    {
        const string reply = "Understood — let me know if anything still feels off.";
        string s = ReplyComplianceScrubber.Scrub(reply, "ReportComfort", actuated: false, "i'm ok now", uiEnglishPin: true);
        Assert.Equal(reply, s);
    }

    [Fact]
    public void Scrub_ZhPinned_FullyEnglishReply_SwappedToChinese()
    {
        // Symmetric drift: ZH UI but the model answered entirely in English.
        string s = ReplyComplianceScrubber.Scrub("Got it, everything looks stable right now.", "ReportComfort", actuated: false, "我還好", uiEnglishPin: false);
        Assert.True(HasCjk(s), $"ZH-pinned reply had no Han: {s}");
    }

    [Fact]
    public void Scrub_ZhPinned_ChineseReply_PassesThrough()
    {
        string s = ReplyComplianceScrubber.Scrub("好的，已了解。", "ReportComfort", actuated: false, "我還好", uiEnglishPin: false);
        Assert.True(HasCjk(s));
    }

    // ── FeedbackLexicon fallbacks follow the pin, not the input script ────────────────────────

    [Fact]
    public void BuildFallbackResponse_EnPinned_ChineseInput_ReturnsEnglish()
        => Assert.False(HasCjk(FeedbackLexicon.BuildFallbackResponse("我覺得很暈", "ReportDiscomfort", uiEnglishPin: true)));

    [Fact]
    public void BuildFallbackResponse_ZhPinned_EnglishInput_ReturnsChinese()
        => Assert.True(HasCjk(FeedbackLexicon.BuildFallbackResponse("I feel dizzy", "ReportDiscomfort", uiEnglishPin: false)));

    [Fact]
    public void TryBuildFollowup_EnPinned_ChineseInput_QuestionAndOptionsEnglish()
    {
        var (q, opts) = FeedbackLexicon.TryBuildFollowup("我不舒服", "Unknown", 2, uiEnglishPin: true);
        Assert.NotNull(q);
        Assert.NotNull(opts);
        Assert.False(HasCjk(q));
        Assert.All(opts!, o => Assert.False(HasCjk(o)));
    }

    [Fact]
    public void TryBuildFollowup_ZhPinned_EnglishInput_OptionsCarryChinese()
    {
        var (_, opts) = FeedbackLexicon.TryBuildFollowup("i don't feel right", "Unknown", 2, uiEnglishPin: false);
        Assert.NotNull(opts);
        Assert.Contains(opts!, HasCjk); // bilingual "暈眩 / Dizziness" labels under a ZH UI
    }

    // ── Gate end-to-end: a Chinese model reply under an English UI exits the gate in English ──

    [Fact]
    public void Gate_EnPinned_ModelRepliedChinese_ResponseIsEnglish()
    {
        var r = NluComplianceGate.Check(
            new NluComplianceGate.RawNluDecision(
                "ReportComfort", 1, "你好，目前看起來很穩定。",
                null, null, null, null, null, NluComplianceGate.DecisionSource.Model),
            "hi there", uiEnglishPin: true);
        Assert.False(HasCjk(r.Response), $"gate shipped Han under EN pin: {r.Response}");
    }
}
