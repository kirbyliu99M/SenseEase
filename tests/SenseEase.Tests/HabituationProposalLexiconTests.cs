using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 reliability: contract tests for the deterministic lexicon that
/// catches explicit profile-override request phrases the LLM may miss.
/// Pinning the small-but-finite vocabulary so we don't lose detection
/// on common requests over time.
/// </summary>
public class HabituationProposalLexiconTests
{
    private static HabituationProfileSnapshot MakeSnapshot(
        string profile = "TYPICAL", double scale = 1.0, int strikeLimit = 2)
        => new HabituationProfileSnapshot
        {
            Profile = profile,
            PersonalPressureScale = scale,
            SevereStrikeLimit = strikeLimit,
        };

    // ── No detection paths ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("我有點頭暈")]
    [InlineData("how are you")]
    public void TryDetect_NonRequest_ReturnsNull(string? input)
    {
        var result = HabituationProposalLexicon.TryDetect(input, MakeSnapshot());
        Assert.Null(result);
    }

    // ── AdjustSensitivity: relax ────────────────────────────────────────────

    [Theory]
    [InlineData("把 sensitivity 調鬆一點")]
    [InlineData("可以把感測度放鬆嗎")]
    [InlineData("我覺得 sensitivity 太敏感，可以降低嗎？")]
    [InlineData("please loosen sensitivity")]
    [InlineData("can you relax sensitivity a bit")]
    [InlineData("lower the sensitivity please")]
    public void TryDetect_RelaxSensitivity_ProducesAdjustSensitivityProposal(string input)
    {
        var snapshot = MakeSnapshot(scale: 1.0);
        var result = HabituationProposalLexicon.TryDetect(input, snapshot);
        Assert.NotNull(result);
        Assert.Equal(OverrideIntent.AdjustSensitivity, result!.Intent);
        Assert.Equal("personal_pressure_scale", result.TargetField);
        Assert.Equal(0.9, Assert.IsType<double>(result.ProposedValue));
    }

    [Fact]
    public void TryDetect_RelaxSensitivity_ClampsAtMin()
    {
        var snapshot = MakeSnapshot(scale: 0.75);
        var result = HabituationProposalLexicon.TryDetect("把 sensitivity 調鬆", snapshot);
        Assert.NotNull(result);
        // 0.75 - 0.10 = 0.65 → clamped to 0.7 (min)
        Assert.Equal(0.7, Assert.IsType<double>(result!.ProposedValue));
    }

    [Fact]
    public void TryDetect_RelaxSensitivity_AlreadyAtMin_ReturnsNull()
    {
        // No room to relax further → no proposal (avoid spam-proposing 0.7→0.7)
        var snapshot = MakeSnapshot(scale: 0.7);
        var result = HabituationProposalLexicon.TryDetect("把 sensitivity 調鬆", snapshot);
        Assert.Null(result);
    }

    // ── AdjustSensitivity: tighten ──────────────────────────────────────────

    [Theory]
    [InlineData("把 sensitivity 調緊一點")]
    [InlineData("感測度收緊")]
    [InlineData("提高 sensitivity")]
    [InlineData("tighten sensitivity")]
    [InlineData("raise sensitivity")]
    public void TryDetect_TightenSensitivity_ProducesAdjustSensitivityProposal(string input)
    {
        var snapshot = MakeSnapshot(scale: 1.0);
        var result = HabituationProposalLexicon.TryDetect(input, snapshot);
        Assert.NotNull(result);
        Assert.Equal(OverrideIntent.AdjustSensitivity, result!.Intent);
        Assert.Equal(1.1, Assert.IsType<double>(result.ProposedValue));
    }

    // ── SwitchProfile ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("switch to RESILIENT profile", "RESILIENT")]
    [InlineData("change profile to TYPICAL", "TYPICAL")]
    [InlineData("set to sensitive", "SENSITIVE")]
    [InlineData("我想切換到 resilient", "RESILIENT")]
    [InlineData("換成敏感模式", "SENSITIVE")]
    public void TryDetect_SwitchProfile_ProducesProposal(string input, string expectedProfile)
    {
        var result = HabituationProposalLexicon.TryDetect(input, MakeSnapshot());
        Assert.NotNull(result);
        Assert.Equal(OverrideIntent.SwitchProfile, result!.Intent);
        Assert.Equal("profile", result.TargetField);
        Assert.Equal(expectedProfile, Assert.IsType<string>(result.ProposedValue));
    }

    [Fact]
    public void TryDetect_ProfileNameMentionedWithoutSwitchVerb_NoProposal()
    {
        // "I feel sensitive today" mentions a profile name but isn't a switch request.
        var result = HabituationProposalLexicon.TryDetect(
            "I feel sensitive today",
            MakeSnapshot());
        Assert.Null(result);
    }

    // ── AdjustStrikeLimit ───────────────────────────────────────────────────

    [Theory]
    [InlineData("raise strike limit")]
    [InlineData("提高嚴重事件容忍度")]
    [InlineData("可以提高 strike limit 嗎")]
    public void TryDetect_RaiseStrikeLimit_ProducesProposal(string input)
    {
        var snapshot = MakeSnapshot(strikeLimit: 2);
        var result = HabituationProposalLexicon.TryDetect(input, snapshot);
        Assert.NotNull(result);
        Assert.Equal(OverrideIntent.AdjustStrikeLimit, result!.Intent);
        Assert.Equal("severe_strike_limit", result.TargetField);
        Assert.Equal(3L, Assert.IsType<long>(result.ProposedValue));
    }

    [Theory]
    [InlineData("lower strike limit")]
    [InlineData("降低嚴重事件容忍度")]
    public void TryDetect_LowerStrikeLimit_ProducesProposal(string input)
    {
        var snapshot = MakeSnapshot(strikeLimit: 3);
        var result = HabituationProposalLexicon.TryDetect(input, snapshot);
        Assert.NotNull(result);
        Assert.Equal(OverrideIntent.AdjustStrikeLimit, result!.Intent);
        Assert.Equal(2L, Assert.IsType<long>(result.ProposedValue));
    }

    [Fact]
    public void TryDetect_StrikeLimitAlreadyAtMax_ReturnsNull()
    {
        var snapshot = MakeSnapshot(strikeLimit: 5);
        var result = HabituationProposalLexicon.TryDetect("raise strike limit", snapshot);
        Assert.Null(result);
    }

    [Fact]
    public void TryDetect_StrikeLimitAlreadyAtMin_ReturnsNull()
    {
        var snapshot = MakeSnapshot(strikeLimit: 1);
        var result = HabituationProposalLexicon.TryDetect("lower strike limit", snapshot);
        Assert.Null(result);
    }

    // ── Expiry ──────────────────────────────────────────────────────────────

    // ── ProfilePickerRequest detection ───────────────────────────────────────

    [Theory]
    [InlineData("profile")]
    [InlineData("Profile")]
    [InlineData("PROFILE")]
    [InlineData("profile?")]
    [InlineData("Profile？")]
    [InlineData("  profile  ")]
    public void TryDetectProfilePickerRequest_BareWord_True(string input)
    {
        Assert.True(HabituationProposalLexicon.TryDetectProfilePickerRequest(input));
    }

    [Theory]
    [InlineData("我想換 profile")]
    [InlineData("show me profile options")]
    [InlineData("what profiles are available")]
    [InlineData("有哪些 profile")]
    [InlineData("which profile should I use")]
    public void TryDetectProfilePickerRequest_GenericRequest_True(string input)
    {
        Assert.True(HabituationProposalLexicon.TryDetectProfilePickerRequest(input));
    }

    [Theory]
    [InlineData("switch to RESILIENT")]
    [InlineData("change profile to TYPICAL")]
    [InlineData("換成敏感模式")]
    public void TryDetectProfilePickerRequest_SpecificProfile_False(string input)
    {
        // Specific profile named → routes through TryDetect (lexicon-built
        // SwitchProfile proposal), NOT the picker.
        Assert.False(HabituationProposalLexicon.TryDetectProfilePickerRequest(input));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("我頭暈")]
    [InlineData("how are you")]
    [InlineData("")]
    public void TryDetectProfilePickerRequest_UnrelatedInput_False(string? input)
    {
        Assert.False(HabituationProposalLexicon.TryDetectProfilePickerRequest(input));
    }

    [Fact]
    public void TryDetect_Proposal_HasExpiryWithin48HoursOfNow()
    {
        var before = System.DateTime.UtcNow;
        var result = HabituationProposalLexicon.TryDetect(
            "switch to RESILIENT profile",
            MakeSnapshot());
        var after = System.DateTime.UtcNow;
        Assert.NotNull(result);
        Assert.InRange(
            result!.ExpiresAtUtc,
            before.AddHours(47).AddMinutes(59),
            after.AddHours(48).AddMinutes(1));
    }
}
