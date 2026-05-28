using System;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 (Phase 7b): contract tests for HabituationOverrideParser. The parser
/// extracts the three structured-token block types the chatbot LLM emits
/// inside its visible reply ([PROFILE_OVERRIDE], [OBSERVATION_ADD],
/// [RESUME_ACKNOWLEDGE]) and is the entry point of the write-side pipeline.
/// Phase 7c/7d/7e build on top of this; Phase 8 E2E scenarios cover the
/// full proposal→confirm→apply path. These tests pin the contract
/// independent of LLM behaviour by feeding hand-crafted reply strings.
/// </summary>
public class HabituationOverrideParserTests
{
    // ── TryParse: empty / no-blocks paths ────────────────────────────────────

    [Fact]
    public void TryParse_Null_ReturnsEmptyResult()
    {
        var parsed = HabituationOverrideParser.TryParse(null);
        Assert.Null(parsed.Proposal);
        Assert.Empty(parsed.Observations);
        Assert.Null(parsed.ResumeAck);
    }

    [Fact]
    public void TryParse_Whitespace_ReturnsEmptyResult()
    {
        var parsed = HabituationOverrideParser.TryParse("   \n\t  ");
        Assert.Null(parsed.Proposal);
        Assert.Empty(parsed.Observations);
        Assert.Null(parsed.ResumeAck);
    }

    [Fact]
    public void TryParse_PlainReplyNoBlocks_AllSubFieldsAbsent()
    {
        var parsed = HabituationOverrideParser.TryParse(
            "Just a normal conversational reply with no structured tokens.");
        Assert.Null(parsed.Proposal);
        Assert.Empty(parsed.Observations);
        Assert.Null(parsed.ResumeAck);
    }

    // ── PROFILE_OVERRIDE happy paths ─────────────────────────────────────────

    [Fact]
    public void TryParse_AdjustSensitivityProposal_ExtractsAllFields()
    {
        const string reply = @"Sure, here's my suggestion.
[PROFILE_OVERRIDE]
{
  ""intent"": ""adjust_sensitivity"",
  ""target_field"": ""personal_pressure_scale"",
  ""proposed_value"": 1.1,
  ""rationale"": ""trajectory shows steady habituation over the last 5 days"",
  ""expires_at_iso"": ""2026-06-02T12:00:00Z""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustSensitivity, parsed.Proposal!.Intent);
        Assert.Equal("personal_pressure_scale", parsed.Proposal.TargetField);
        Assert.Equal(1.1, Assert.IsType<double>(parsed.Proposal.ProposedValue));
        Assert.Contains("habituation", parsed.Proposal.Rationale);
        Assert.Equal(DateTimeKind.Utc, parsed.Proposal.ExpiresAtUtc.Kind);
        Assert.Equal(2026, parsed.Proposal.ExpiresAtUtc.Year);
    }

    [Fact]
    public void TryParse_SwitchProfileProposal_StringValueExtracted()
    {
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""switch_profile"",
  ""target_field"": ""profile"",
  ""proposed_value"": ""TYPICAL"",
  ""rationale"": ""你的反應穩定許多"",
  ""expires_at_iso"": ""2026-06-02T00:00:00Z""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.SwitchProfile, parsed.Proposal!.Intent);
        Assert.Equal("TYPICAL", Assert.IsType<string>(parsed.Proposal.ProposedValue));
    }

    [Fact]
    public void TryParse_AdjustStrikeLimitProposal_IntegerValueExtracted()
    {
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""adjust_strike_limit"",
  ""target_field"": ""severe_strike_limit"",
  ""proposed_value"": 3,
  ""rationale"": ""提升容忍度"",
  ""expires_at_iso"": ""2026-06-02T00:00:00Z""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustStrikeLimit, parsed.Proposal!.Intent);
        // Integer values arrive as long via TryGetInt64.
        Assert.Equal(3L, Assert.IsType<long>(parsed.Proposal.ProposedValue));
    }

    // ── PROFILE_OVERRIDE rejection paths ─────────────────────────────────────

    [Fact]
    public void TryParse_UnknownIntent_DropsProposal()
    {
        // Q4.5 locked: ResetOffset is NOT a valid OverrideIntent; the parser
        // silently drops PROFILE_OVERRIDE blocks whose intent doesn't map.
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""reset_offset"",
  ""target_field"": ""offset"",
  ""proposed_value"": 0,
  ""rationale"": ""start over"",
  ""expires_at_iso"": ""2026-06-02T00:00:00Z""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.Null(parsed.Proposal);
    }

    [Fact]
    public void TryParse_MalformedJson_DropsProposal()
    {
        const string reply = @"[PROFILE_OVERRIDE]
{ this is not valid json at all
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.Null(parsed.Proposal);
    }

    [Fact]
    public void TryParse_MissingExpiresAt_DropsProposal()
    {
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""adjust_sensitivity"",
  ""target_field"": ""personal_pressure_scale"",
  ""proposed_value"": 1.1,
  ""rationale"": ""no timestamp""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.Null(parsed.Proposal);
    }

    [Fact]
    public void TryParse_MissingOptionalFields_StillExtracts()
    {
        // No target_field, no rationale — minimum required keys are intent +
        // expires_at_iso. Parser tolerates missing strings (default empty).
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""adjust_sensitivity"",
  ""proposed_value"": 1.05,
  ""expires_at_iso"": ""2026-06-02T00:00:00Z""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(string.Empty, parsed.Proposal!.TargetField);
        Assert.Equal(string.Empty, parsed.Proposal.Rationale);
    }

    [Fact]
    public void TryParse_UnknownJsonKey_Ignored()
    {
        // Additive schema evolution: parser ignores unknown keys.
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""adjust_sensitivity"",
  ""target_field"": ""personal_pressure_scale"",
  ""proposed_value"": 1.1,
  ""rationale"": ""ok"",
  ""expires_at_iso"": ""2026-06-02T00:00:00Z"",
  ""future_field_v2"": { ""sub_key"": [1,2,3] }
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
    }

    [Fact]
    public void TryParse_IntentCaseInsensitive()
    {
        const string reply = @"[PROFILE_OVERRIDE]
{
  ""intent"": ""Adjust_Sensitivity"",
  ""target_field"": ""personal_pressure_scale"",
  ""proposed_value"": 1.1,
  ""rationale"": ""ok"",
  ""expires_at_iso"": ""2026-06-02T00:00:00Z""
}
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustSensitivity, parsed.Proposal!.Intent);
    }

    [Fact]
    public void TryParse_TwoProposals_FirstWins()
    {
        // Spec §4.3.1: "每個 LLM response 最多含一個 PROFILE_OVERRIDE".
        // Parser is tolerant of model misbehaviour and takes the FIRST.
        const string reply = @"[PROFILE_OVERRIDE]
{ ""intent"": ""adjust_sensitivity"", ""target_field"": ""x"", ""proposed_value"": 1.1,
  ""rationale"": ""first"", ""expires_at_iso"": ""2026-06-02T00:00:00Z"" }
[/PROFILE_OVERRIDE]
some text
[PROFILE_OVERRIDE]
{ ""intent"": ""switch_profile"", ""target_field"": ""profile"", ""proposed_value"": ""SENSITIVE"",
  ""rationale"": ""second"", ""expires_at_iso"": ""2026-06-02T00:00:00Z"" }
[/PROFILE_OVERRIDE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustSensitivity, parsed.Proposal!.Intent);
        Assert.Equal("first", parsed.Proposal.Rationale);
    }

    // ── OBSERVATION_ADD paths ────────────────────────────────────────────────

    [Fact]
    public void TryParse_SingleObservation_Extracted()
    {
        const string reply = @"Thanks for sharing.
[OBSERVATION_ADD]
User reports symptoms tend to worsen on Mondays
[/OBSERVATION_ADD]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        var obs = Assert.Single(parsed.Observations);
        Assert.Contains("Mondays", obs);
    }

    [Fact]
    public void TryParse_TwoObservations_BothExtracted()
    {
        const string reply = @"[OBSERVATION_ADD]
first observation
[/OBSERVATION_ADD]
text
[OBSERVATION_ADD]
second observation
[/OBSERVATION_ADD]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.Equal(2, parsed.Observations.Count);
        Assert.Equal("first observation", parsed.Observations[0]);
        Assert.Equal("second observation", parsed.Observations[1]);
    }

    [Fact]
    public void TryParse_ThreeObservations_OnlyFirstTwoKept()
    {
        // Spec §4.3.1 caps OBSERVATION_ADD at 0-2 per response.
        const string reply = @"[OBSERVATION_ADD]a[/OBSERVATION_ADD]
[OBSERVATION_ADD]b[/OBSERVATION_ADD]
[OBSERVATION_ADD]c[/OBSERVATION_ADD]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.Equal(2, parsed.Observations.Count);
        Assert.Equal("a", parsed.Observations[0]);
        Assert.Equal("b", parsed.Observations[1]);
    }

    [Fact]
    public void TryParse_EmptyObservation_Skipped()
    {
        const string reply = @"[OBSERVATION_ADD]   [/OBSERVATION_ADD]
[OBSERVATION_ADD]real one[/OBSERVATION_ADD]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        var obs = Assert.Single(parsed.Observations);
        Assert.Equal("real one", obs);
    }

    // ── RESUME_ACKNOWLEDGE paths ─────────────────────────────────────────────

    [Fact]
    public void TryParse_ResumeAcknowledgeTrue_Extracted()
    {
        const string reply = @"[RESUME_ACKNOWLEDGE]
{ ""user_confirmed"": true, ""user_quote"": ""I'm ready to resume"" }
[/RESUME_ACKNOWLEDGE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.ResumeAck);
        Assert.True(parsed.ResumeAck!.UserConfirmed);
        Assert.Equal("I'm ready to resume", parsed.ResumeAck.UserQuote);
    }

    [Fact]
    public void TryParse_ResumeAcknowledgeFalse_Extracted()
    {
        const string reply = @"[RESUME_ACKNOWLEDGE]
{ ""user_confirmed"": false, ""user_quote"": ""還不行"" }
[/RESUME_ACKNOWLEDGE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.ResumeAck);
        Assert.False(parsed.ResumeAck!.UserConfirmed);
    }

    [Fact]
    public void TryParse_ResumeAcknowledgeMissingConfirmedField_Dropped()
    {
        // user_confirmed is required per spec.
        const string reply = @"[RESUME_ACKNOWLEDGE]
{ ""user_quote"": ""maybe later"" }
[/RESUME_ACKNOWLEDGE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.Null(parsed.ResumeAck);
    }

    // ── Combined block types ─────────────────────────────────────────────────

    [Fact]
    public void TryParse_AllThreeBlockTypes_AllExtracted()
    {
        const string reply = @"Here is my full response.
[PROFILE_OVERRIDE]
{ ""intent"": ""adjust_sensitivity"", ""target_field"": ""personal_pressure_scale"",
  ""proposed_value"": 1.1, ""rationale"": ""ok"",
  ""expires_at_iso"": ""2026-06-02T00:00:00Z"" }
[/PROFILE_OVERRIDE]
[OBSERVATION_ADD]user mentions weekends are harder[/OBSERVATION_ADD]
[RESUME_ACKNOWLEDGE]
{ ""user_confirmed"": true, ""user_quote"": ""go ahead"" }
[/RESUME_ACKNOWLEDGE]";
        var parsed = HabituationOverrideParser.TryParse(reply);
        Assert.NotNull(parsed.Proposal);
        Assert.Single(parsed.Observations);
        Assert.NotNull(parsed.ResumeAck);
    }

    // ── StripAllOverrideBlocks ───────────────────────────────────────────────

    [Fact]
    public void StripAllOverrideBlocks_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HabituationOverrideParser.StripAllOverrideBlocks(null));
    }

    [Fact]
    public void StripAllOverrideBlocks_NoBlocks_ReturnsInputTrimmed()
    {
        const string reply = "   plain reply   ";
        Assert.Equal("plain reply", HabituationOverrideParser.StripAllOverrideBlocks(reply));
    }

    [Fact]
    public void StripAllOverrideBlocks_AllThreeBlockTypes_Removed()
    {
        const string reply = @"Visible part one.
[PROFILE_OVERRIDE]
{ ""intent"": ""adjust_sensitivity"", ""expires_at_iso"": ""2026-06-02T00:00:00Z"" }
[/PROFILE_OVERRIDE]
Visible part two.
[OBSERVATION_ADD]a thing[/OBSERVATION_ADD]
Visible part three.
[RESUME_ACKNOWLEDGE]
{ ""user_confirmed"": true }
[/RESUME_ACKNOWLEDGE]
Visible tail.";
        string stripped = HabituationOverrideParser.StripAllOverrideBlocks(reply);
        Assert.DoesNotContain("PROFILE_OVERRIDE", stripped);
        Assert.DoesNotContain("OBSERVATION_ADD", stripped);
        Assert.DoesNotContain("RESUME_ACKNOWLEDGE", stripped);
        Assert.DoesNotContain("adjust_sensitivity", stripped);
        Assert.Contains("Visible part one", stripped);
        Assert.Contains("Visible part two", stripped);
        Assert.Contains("Visible part three", stripped);
        Assert.Contains("Visible tail", stripped);
    }

    [Fact]
    public void StripAllOverrideBlocks_CollapsesBlankLineTower()
    {
        // After stripping, consecutive blank lines collapse to one so the
        // visible reply doesn't read as a wall of empty whitespace.
        const string reply = @"line one


[PROFILE_OVERRIDE]
{ ""intent"": ""adjust_sensitivity"", ""expires_at_iso"": ""2026-06-02T00:00:00Z"" }
[/PROFILE_OVERRIDE]


line two";
        string stripped = HabituationOverrideParser.StripAllOverrideBlocks(reply);
        Assert.DoesNotContain("\n\n\n", stripped);
        Assert.Contains("line one", stripped);
        Assert.Contains("line two", stripped);
    }
}
