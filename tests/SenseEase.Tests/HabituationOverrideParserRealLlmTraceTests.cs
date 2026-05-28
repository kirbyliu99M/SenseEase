using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Regression pin: parser must handle the exact raw outputs Mistral 3B
/// has produced in end-to-end tests. Each case is a verbatim raw string
/// from model_trace.jsonl so a future parser refactor can't silently
/// regress one of these previously-working scenarios.
/// </summary>
public class HabituationOverrideParserRealLlmTraceTests
{
    [Fact]
    public void TryParse_RealTrace_2026_05_28_T18_04_56_AdjustSensitivity_Extracted()
    {
        // Verbatim raw output from %LOCALAPPDATA%/SenseEase/model_trace.jsonl
        // at 18:04:56 — clean PROFILE_OVERRIDE block, post-StripMarkdownFences.
        const string raw =
            "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"您過去五天的 tier 持續在 6-7 穩定，我建議將 sensitivity 調降至 0.9。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
            "[PROFILE_OVERRIDE]\n" +
            "{\"intent\":\"adjust_sensitivity\",\"target_field\":\"personal_pressure_scale\",\"proposed_value\":0.9,\"rationale\":\"用戶過去五天 tier 穩定在 6-7，可適度放鬆壓力感知敏感度\",\"expires_at_iso\":\"2026-06-03T17:00:00Z\"}\n" +
            "[/PROFILE_OVERRIDE]";

        var parsed = HabituationOverrideParser.TryParse(raw);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustSensitivity, parsed.Proposal!.Intent);
        Assert.Equal("personal_pressure_scale", parsed.Proposal.TargetField);
        Assert.Equal(0.9, Assert.IsType<double>(parsed.Proposal.ProposedValue));
    }

    [Fact]
    public void TryParse_RealTrace_2026_05_28_T18_21_TruncatedObservation_StillExtractsProposal()
    {
        // Raw from 18:21:00 — PROFILE_OVERRIDE complete, OBSERVATION_ADD
        // truncated mid-stream because max_tokens=256 cut the model off.
        // Parser must still extract the complete proposal block.
        const string raw =
            "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"看到您過去五天的 tier 維持在 6-7 穩定...\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
            "[PROFILE_OVERRIDE]\n" +
            "{\"intent\":\"adjust_sensitivity\",\"target_field\":\"personal_pressure_scale\",\"proposed_value\":0.95,\"rationale\":\"用戶習慣狀態顯示近期穩定\",\"expires_at_iso\":\"2026-06-04T17:00:00Z\"}\n" +
            "[/PROFILE_OVERRIDE]\n" +
            "[OBSERVATION_ADD";

        var parsed = HabituationOverrideParser.TryParse(raw);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(0.95, Assert.IsType<double>(parsed.Proposal!.ProposedValue));
        // Truncated OBSERVATION_ADD has no closing tag → not extracted.
        Assert.Empty(parsed.Observations);
    }

    [Fact]
    public void TryParse_RealTrace_2026_05_28_T17_58_OversightTagVariant_StillExtracted()
    {
        // Raw from 17:58:22 — model hallucinated PROFILE_OVERSIGHT tag.
        // Tag-tolerance net must catch this.
        const string raw =
            "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"...\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
            "[PROFILE_OVERSIGHT]\n" +
            "{\"intent\":\"adjust_sensitivity\",\"target_field\":\"personal_pressure_scale\",\"proposed_value\":0.9,\"rationale\":\"...\",\"expires_at_iso\":\"2026-06-03T17:00:00Z\"}\n" +
            "[/PROFILE_OVERSIGHT]";

        var parsed = HabituationOverrideParser.TryParse(raw);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustSensitivity, parsed.Proposal!.Intent);
    }
}
