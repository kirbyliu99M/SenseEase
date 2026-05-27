using System.Collections.Generic;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M2 (Phase 6a/6b): contract tests for the [habituation_state] block
/// format. The block is the chatbot's primary read-side of habituation
/// engine state — Phase 6d will document it in the LLM system prompt
/// and Phase 8 scenarios verify the LLM actually grounds replies in it.
///
/// <para>These tests pin the LITERAL block format because:</para>
/// <list type="bullet">
///   <item>The MistralChatbotService SystemPrompt (post-Phase 6d) will
///         reference specific field names — a rename here would silently
///         desync the prompt's rules from the actual block content.</item>
///   <item>Token budget is tight (~120 tokens / ~600 chars target). Tests
///         pin the conditional-field optimisations so a future "always
///         include this field" change is flagged.</item>
/// </list>
/// </summary>
public class HabituationStateBlockBuilderTests
{
    private static HabituationProfileSnapshot MinimalSnapshot() => new()
    {
        Profile          = "RESILIENT",
        CurrentVimsTier  = 1,
        HabituationLevel = 0,
        EngineState      = "RUNNING",
    };

    [Fact]
    public void Build_NullSnapshot_ReturnsEmptyString()
    {
        // Caller convention: empty block string means "don't prepend".
        var result = HabituationStateBlockBuilder.BuildBlock(null, null, false);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Build_MinimalSnapshot_OutputsRequiredFields()
    {
        var snap = MinimalSnapshot();
        snap.CurrentVimsTier  = 4;
        snap.HabituationLevel = 12.3;

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.StartsWith("[habituation_state]\n", block);
        Assert.EndsWith("[/habituation_state]", block);
        Assert.Contains("tier: 4\n", block);
        Assert.Contains("level: 12.3\n", block);
        Assert.Contains("profile: RESILIENT\n", block);
        Assert.Contains("state: RUNNING\n", block);
    }

    [Fact]
    public void Build_NoOptionalsTriggered_OmitsConditionalLines()
    {
        // Defaults: AwaitingUserConfirm=false, no power save, no advisory,
        // PersonalPressureScale=1.0, no observations. None of those
        // should appear in the output (token budget discipline).
        string block = HabituationStateBlockBuilder.BuildBlock(
            MinimalSnapshot(), trajectorySummary: null, powerSaveActive: false);

        Assert.DoesNotContain("awaiting_user_confirm", block);
        Assert.DoesNotContain("power_save_active", block);
        Assert.DoesNotContain("advisory:", block);
        Assert.DoesNotContain("scale:", block);
        Assert.DoesNotContain("trajectory:", block);
        Assert.DoesNotContain("recent_observations:", block);
    }

    [Fact]
    public void Build_AwaitingUserConfirm_IncludesFlag()
    {
        var snap = MinimalSnapshot();
        snap.AwaitingUserConfirm = true;
        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);
        Assert.Contains("awaiting_user_confirm: true\n", block);
    }

    [Fact]
    public void Build_PowerSaveActive_IncludesFlag()
    {
        string block = HabituationStateBlockBuilder.BuildBlock(
            MinimalSnapshot(), null, powerSaveActive: true);
        Assert.Contains("power_save_active: true\n", block);
    }

    [Fact]
    public void Build_VestibularAdvisory_IncludesLowercasedField()
    {
        var snap = MinimalSnapshot();
        snap.MedicalAdvisoryShown = MedicalAdvisory.Vestibular;
        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);
        Assert.Contains("advisory: vestibular\n", block);
        Assert.DoesNotContain("Vestibular", block);  // pin lowercase
    }

    [Fact]
    public void Build_LastCalibrationIso_TruncatesToDate()
    {
        var snap = MinimalSnapshot();
        snap.LastCalibrationAtIso = "2026-04-10T14:23:45.1234567Z";
        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);
        Assert.Contains("last_cal: 2026-04-10\n", block);
        Assert.DoesNotContain("14:23", block);  // no time portion
    }

    [Fact]
    public void Build_ScaleAtDefault_OmittedFromBlock()
    {
        var snap = MinimalSnapshot();
        snap.PersonalPressureScale = 1.0;
        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);
        Assert.DoesNotContain("scale:", block);
    }

    [Fact]
    public void Build_ScaleShifted_IncludesField()
    {
        var snap = MinimalSnapshot();
        snap.PersonalPressureScale = 0.85;
        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);
        Assert.Contains("scale: 0.85\n", block);
    }

    [Fact]
    public void Build_TrajectorySummary_IncludesAsLine()
    {
        string traj = "level rose 8.0 → 12.0 over 14d, 3 spikes";
        string block = HabituationStateBlockBuilder.BuildBlock(MinimalSnapshot(), traj, false);
        Assert.Contains($"trajectory: {traj}\n", block);
    }

    [Fact]
    public void Build_LongitudinalObservations_MostRecentFirstMaxFive()
    {
        var snap = MinimalSnapshot();
        // Append-order in snapshot is oldest → newest. Block emits
        // most-recent first, capped at MaxObservations (5).
        snap.LongitudinalObservations = new List<string>
        {
            "2026-04-01: oldest entry",
            "2026-04-15: second oldest",
            "2026-05-01: middle",
            "2026-05-10: getting newer",
            "2026-05-15: even newer",
            "2026-05-20: newest entry",
        };

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.Contains("recent_observations:\n", block);
        // Most recent first
        int idxNewest = block.IndexOf("newest entry");
        int idxMiddle = block.IndexOf("middle");
        Assert.True(idxNewest > 0);
        Assert.True(idxMiddle > 0);
        Assert.True(idxNewest < idxMiddle, "newest should appear before older entries");
        // Oldest dropped (only 5 most recent)
        Assert.DoesNotContain("oldest entry", block);
    }

    [Fact]
    public void Build_EmptyObservationsList_OmitsSection()
    {
        var snap = MinimalSnapshot();
        snap.LongitudinalObservations = new List<string>();
        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);
        Assert.DoesNotContain("recent_observations:", block);
    }

    [Fact]
    public void Build_FullSnapshot_StaysUnderTokenBudget()
    {
        // Belt-and-suspenders: even with every field populated + 5 long-ish
        // observations, the block must fit inside MaxBlockChars (~600 chars
        // ≈ 150 tokens). Phase 6 prompt budget breakdown depends on this.
        var snap = new HabituationProfileSnapshot
        {
            Profile             = "SENSITIVE",
            CurrentVimsTier     = 8,
            HabituationLevel    = 14.7,
            EngineState         = "PAUSED_AWAITING_USER_CONFIRM",
            AwaitingUserConfirm = true,
            CalibrationCount    = 3,
            LastCalibrationAtIso = "2026-04-10T14:23:00.0000000Z",
            MedicalAdvisoryShown = MedicalAdvisory.Vestibular,
            PersonalPressureScale = 0.85,
            LongitudinalObservations = new List<string>
            {
                "2026-04-20: user reports headaches concentrated on Monday mornings",
                "2026-05-01: user mentions improvement with new monitor distance",
                "2026-05-08: tier dropped to 4 after taking a 5-day break",
                "2026-05-15: user reports baseline stable around tier 5-6",
                "2026-05-20: morning calibration showed reduced sensitivity",
            },
        };

        string block = HabituationStateBlockBuilder.BuildBlock(
            snap,
            trajectorySummary: "level rose 8.0 → 14.7 over 28d, 2 spikes",
            powerSaveActive: true);

        Assert.True(block.Length <= HabituationStateBlockBuilder.MaxBlockChars,
            $"Block exceeded {HabituationStateBlockBuilder.MaxBlockChars} chars (actual: {block.Length})");
        // Must still be a complete block (closing marker intact)
        Assert.EndsWith("[/habituation_state]", block);
    }

    [Fact]
    public void Build_OversizedObservations_TruncatesPreservingCloser()
    {
        // Pathological input: a single observation longer than MaxBlockChars.
        // Builder must still emit a complete-looking block (closing marker
        // intact) rather than a raw truncation that leaves an open tag.
        var snap = MinimalSnapshot();
        string huge = new string('X', 1200);
        snap.LongitudinalObservations = new List<string> { huge };

        string block = HabituationStateBlockBuilder.BuildBlock(snap, null, false);

        Assert.True(block.Length <= HabituationStateBlockBuilder.MaxBlockChars);
        Assert.EndsWith("[/habituation_state]", block);
    }
}
