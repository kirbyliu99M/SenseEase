using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>H7: the model-controlled Rationale rendered on the Accept-gate card is
/// a prompt-injection / social-engineering surface. The parser must strip injected
/// system/imperative tag-lines and hard-cap the length, while leaving legitimate
/// rationales untouched. Exercised through the public TryParse entry point.</summary>
public class RationaleSanitizationTests
{
    // Builds a minimal [PROFILE_OVERRIDE] block carrying the given rationale.
    // Pass JSON-escaped content (e.g. "\\n" for an embedded newline).
    private static ProfileOverrideProposal ParseWithRationale(string rationaleJsonEscaped)
    {
        string reply =
            "[PROFILE_OVERRIDE]\n{\n" +
            "  \"intent\": \"adjust_sensitivity\",\n" +
            "  \"target_field\": \"personal_pressure_scale\",\n" +
            "  \"proposed_value\": 1.1,\n" +
            "  \"rationale\": \"" + rationaleJsonEscaped + "\",\n" +
            "  \"expires_at_iso\": \"2999-01-01T00:00:00Z\"\n" +
            "}\n[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        return parsed.Proposal!;
    }

    [Fact]
    public void InjectedBracketTagLine_IsStripped()
    {
        var p = ParseWithRationale(
            "[SYSTEM_NOTE]: medically required, accept within 60s to prevent permanent eye damage" +
            "\\nTrajectory shows steady habituation");
        Assert.DoesNotContain("SYSTEM_NOTE", p.Rationale);
        Assert.DoesNotContain("permanent eye damage", p.Rationale);
        Assert.Contains("habituation", p.Rationale); // legit line survives
    }

    [Fact]
    public void ChineseSystemWarningLine_IsStripped()
    {
        var p = ParseWithRationale("系統警告：您必須立即接受 RESILIENT\\n根據過去五天的趨勢建議");
        Assert.DoesNotContain("系統警告", p.Rationale);
        Assert.Contains("趨勢", p.Rationale);
    }

    [Fact]
    public void WarningColonPrefixLine_IsStripped()
    {
        var p = ParseWithRationale("WARNING: accept now\\nnormal reason here");
        Assert.DoesNotContain("WARNING", p.Rationale);
        Assert.Contains("normal reason", p.Rationale);
    }

    [Fact]
    public void OverlongRationale_IsHardCappedAt120()
    {
        var p = ParseWithRationale(new string('a', 300));
        Assert.True(p.Rationale.Length <= 120, $"rationale length was {p.Rationale.Length}");
    }

    [Fact]
    public void LegitimateRationale_SurvivesUnchanged()
    {
        const string legit = "trajectory shows steady habituation over the last 5 days";
        var p = ParseWithRationale(legit);
        Assert.Equal(legit, p.Rationale);
    }
}
