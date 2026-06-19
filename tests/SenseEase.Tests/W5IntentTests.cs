using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Nlp;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>W5 chatbot-intent correctness gates: shader-hint sign (NE4),
/// profile-switch substring collision (NE5), and the proposal-envelope JSON
/// extraction (EM6).</summary>
public class W5IntentTests
{
    // ── NE4: "darker" deepens (positive), "lighter" lightens (negative) ──────
    [Fact]
    public void NE4_Darker_DeepensMask_Positive()
    {
        Assert.True(FeedbackLexicon.ClassifyShaderHints("太亮").maskDepthDelta > 0);
    }

    [Fact]
    public void NE4_Lighter_LightensMask_Negative()
    {
        Assert.True(FeedbackLexicon.ClassifyShaderHints("太暗").maskDepthDelta < 0);
    }

    [Fact]
    public void NE4_DarkerAndLighter_AreDistinguishable()
    {
        double darker  = FeedbackLexicon.ClassifyShaderHints("再暗一點").maskDepthDelta;
        double lighter = FeedbackLexicon.ClassifyShaderHints("亮一點").maskDepthDelta;
        Assert.NotEqual(darker, lighter);
    }

    // ── NE5: an adjust-sensitivity turn carrying a switch verb ("改成") must
    //         NOT hijack into SwitchProfile SENSITIVE (敏感 ⊂ 敏感度). ──────────
    [Fact]
    public void NE5_ChangeTheSensitivity_IsAdjustSensitivity_NotProfileSwitch()
    {
        var p = HabituationProposalLexicon.TryDetect("把敏感度改成收緊一點", null);
        Assert.False(p is { Intent: OverrideIntent.SwitchProfile }, "must not hijack to SwitchProfile");
        Assert.NotNull(p);
        Assert.Equal(OverrideIntent.AdjustSensitivity, p!.Intent);
    }

    [Fact]
    public void NE5_ExplicitProfileSwitch_NoLongerDetected()
    {
        // CW-3 (honesty): profile-switch detection was removed from the lexicon —
        // profiles are auto-managed, so a profile-switch utterance produces NO
        // proposal (answered upstream by the honest auto-managed reply instead).
        var p = HabituationProposalLexicon.TryDetect("切換到敏感", null);
        Assert.Null(p);
    }

    // ── EM6: ExtractJson returns ONLY the first balanced object, so a trailing
    //         [PROFILE_OVERRIDE]{…} block doesn't poison envelope parsing. ─────
    [Fact]
    public void EM6_ExtractJson_StopsAtFirstBalancedObject()
    {
        string raw = "{\"reply\":\"ok\"}\n[PROFILE_OVERRIDE]\n{\"intent\":\"x\"}\n[/PROFILE_OVERRIDE]";
        Assert.Equal("{\"reply\":\"ok\"}", LocalLlmChatbotService.ExtractJson(raw));
    }

    [Fact]
    public void EM6_ExtractJson_IgnoresBracesInsideStrings()
    {
        string raw = "{\"reply\":\"a } brace in a string\"}\ntrailing text";
        Assert.Equal("{\"reply\":\"a } brace in a string\"}", LocalLlmChatbotService.ExtractJson(raw));
    }

    [Fact]
    public void EM6_ExtractJson_HandlesNestedObjects()
    {
        string raw = "{\"a\":{\"b\":1}}\n[OBSERVATION_ADD]\nfoo\n[/OBSERVATION_ADD]";
        Assert.Equal("{\"a\":{\"b\":1}}", LocalLlmChatbotService.ExtractJson(raw));
    }
}
