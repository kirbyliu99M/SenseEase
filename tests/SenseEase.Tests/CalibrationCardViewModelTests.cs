using System;
using SenseEase.App.ViewModels;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Stage 3 — headless tests for the 5-card state machine that drives
/// the first-time calibration UI. Pins navigation, mutual-exclusivity rules,
/// Q3-mandatory gate, and the BuildResponses pack-out.</summary>
public class CalibrationCardViewModelTests
{
    // ── Navigation ──────────────────────────────────────────────────────────

    [Fact]
    public void StartsOnCard1_BackDisabled_NextEnabled()
    {
        var vm = new CalibrationCardViewModel();
        Assert.Equal(0, vm.CurrentCardIndex);
        Assert.True(vm.IsCard1Visible);
        Assert.False(vm.IsLastCard);
        Assert.True(vm.CanAdvance);
        Assert.False(vm.BackCommand.CanExecute(null));
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.Equal("1 / 5", vm.CardCounterText);
    }

    [Fact]
    public void Next_AdvancesThroughCards_StopsAtFive()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q3Intensity = 3; // satisfy the mandatory gate on card 3
        vm.NextCommand.Execute(null);                       // -> card 2
        Assert.True(vm.IsCard2Visible);
        vm.NextCommand.Execute(null);                       // -> card 3
        vm.NextCommand.Execute(null);                       // -> card 4
        vm.NextCommand.Execute(null);                       // -> card 5
        Assert.True(vm.IsLastCard);
        Assert.True(vm.IsCard5Visible);
        // Card 5: Next is no-op (we don't go past 5).
        vm.NextCommand.Execute(null);
        Assert.Equal(4, vm.CurrentCardIndex);
    }

    [Fact]
    public void Back_GoesBackOneCard()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q3Intensity = 3;
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.CurrentCardIndex);
        vm.BackCommand.Execute(null);
        Assert.Equal(1, vm.CurrentCardIndex);
    }

    // ── Q3 mandatory gate ───────────────────────────────────────────────────

    [Fact]
    public void OnCard3_NextDisabled_UntilQ3IntensitySet()
    {
        var vm = new CalibrationCardViewModel();
        vm.NextCommand.Execute(null);                       // -> card 2
        vm.NextCommand.Execute(null);                       // -> card 3
        Assert.Equal(2, vm.CurrentCardIndex);
        Assert.False(vm.CanAdvance);
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.Q3Intensity = 4;
        Assert.True(vm.CanAdvance);
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.Q3Intensity = null;
        Assert.False(vm.CanAdvance);
    }

    [Fact]
    public void CompleteCommand_OnlyExecutesOnLastCardWithQ3Satisfied()
    {
        var vm = new CalibrationCardViewModel();
        Assert.False(vm.CompleteCommand.CanExecute(null));  // not on last card
        vm.Q3Intensity = 2;
        for (int i = 0; i < 4; i++) vm.NextCommand.Execute(null);
        Assert.True(vm.IsLastCard);
        Assert.True(vm.CompleteCommand.CanExecute(null));
    }

    // ── Q2 mutual exclusivity ───────────────────────────────────────────────

    [Fact]
    public void Q2_PickingNone_ClearsAllSymptoms()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q2Dizziness = true;
        vm.Q2Nausea    = true;
        vm.Q2EyeStrain = true;
        Assert.True(vm.Q2Dizziness);

        vm.Q2None = true;
        Assert.False(vm.Q2Dizziness);
        Assert.False(vm.Q2Nausea);
        Assert.False(vm.Q2EyeStrain);
        Assert.True(vm.Q2None);
    }

    [Fact]
    public void Q2_PickingASymptomAfterNone_ClearsNone()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q2None = true;
        Assert.True(vm.Q2None);
        vm.Q2Headache = true;
        Assert.False(vm.Q2None);
        Assert.True(vm.Q2Headache);
    }

    // ── Q5 mutual exclusivity (two-tier) ────────────────────────────────────

    [Fact]
    public void Q5_PickingNone_ClearsAllRisksAndSkipped()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5MotionSickness = true;
        vm.Q5Skipped = true;          // implicit: this clears everything else
        Assert.False(vm.Q5MotionSickness);
        Assert.True(vm.Q5Skipped);

        vm.Q5None = true;
        Assert.False(vm.Q5Skipped);
        Assert.False(vm.Q5MotionSickness);
        Assert.True(vm.Q5None);
    }

    [Fact]
    public void Q5_PickingSkipped_ClearsAllIncludingNone()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5None = true;
        vm.Q5Skipped = true;
        Assert.False(vm.Q5None);
        Assert.True(vm.Q5Skipped);
    }

    [Fact]
    public void Q5_PickingARisk_ClearsNoneAndSkipped()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5Skipped = true;
        vm.Q5Migraine = true;
        Assert.False(vm.Q5Skipped);
        Assert.False(vm.Q5None);
        Assert.True(vm.Q5Migraine);
    }

    // ── Q4 mutual exclusivity ───────────────────────────────────────────────

    [Fact]
    public void Q4_NoBaseline_ClearsComparison_AndViceVersa()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q4Comparison = BaselineComparison.Worse;
        vm.Q4NoBaseline = true;
        Assert.Null(vm.Q4Comparison);
        Assert.True(vm.Q4NoBaseline);

        vm.Q4Comparison = BaselineComparison.Similar;
        Assert.Equal(BaselineComparison.Similar, vm.Q4Comparison);
        Assert.False(vm.Q4NoBaseline);
    }

    // ── BuildResponses packing ──────────────────────────────────────────────

    [Fact]
    public void BuildResponses_EmptyVm_AllPrimaryFieldsNull()
    {
        var vm = new CalibrationCardViewModel();
        var r = vm.BuildResponses();
        Assert.Null(r.Q1Segment);
        Assert.Null(r.Q2Symptoms);
        Assert.Null(r.Q3Intensity);
        Assert.Null(r.Q4Comparison);
        Assert.False(r.Q4NoBaseline);
        Assert.Null(r.Q5RiskFactors);
    }

    [Fact]
    public void BuildResponses_Q2None_PacksAsEmptyList_NotNull()
    {
        // "都沒有" semantics: explicit empty list, distinct from "didn't answer" (null).
        var vm = new CalibrationCardViewModel();
        vm.Q2None = true;
        var r = vm.BuildResponses();
        Assert.NotNull(r.Q2Symptoms);
        Assert.Empty(r.Q2Symptoms!);
    }

    [Fact]
    public void BuildResponses_Q2MultiSelect_PreservesAllPickedSymptoms()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q2Dizziness = true;
        vm.Q2Nausea    = true;
        vm.Q2EyeStrain = true;
        var r = vm.BuildResponses();
        Assert.NotNull(r.Q2Symptoms);
        Assert.Contains(SymptomType.Dizziness, r.Q2Symptoms!);
        Assert.Contains(SymptomType.Nausea, r.Q2Symptoms!);
        Assert.Contains(SymptomType.EyeStrain, r.Q2Symptoms!);
        Assert.Equal(3, r.Q2Symptoms!.Count);
    }

    [Fact]
    public void BuildResponses_Q5Skipped_PacksAsSkippedSentinel()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5Skipped = true;
        var r = vm.BuildResponses();
        Assert.NotNull(r.Q5RiskFactors);
        Assert.Single(r.Q5RiskFactors!);
        Assert.Equal(RiskFactor.Skipped, r.Q5RiskFactors![0]);
    }

    [Fact]
    public void BuildResponses_Q5None_PacksAsNoneSentinel()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5None = true;
        var r = vm.BuildResponses();
        Assert.NotNull(r.Q5RiskFactors);
        Assert.Single(r.Q5RiskFactors!);
        Assert.Equal(RiskFactor.None, r.Q5RiskFactors![0]);
    }

    [Fact]
    public void BuildResponses_FreetextTrimmedAndEmptyNulled()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q1Freetext = "   trailing space   ";
        vm.Q2Freetext = "   ";   // whitespace-only → null
        vm.Q3Freetext = null;
        var r = vm.BuildResponses();
        Assert.Equal("trailing space", r.Q1Freetext);
        Assert.Null(r.Q2Freetext);
        Assert.Null(r.Q3Freetext);
    }

    [Fact]
    public void BuildResponses_AllSourceTagsSetToButtons()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q3Intensity = 3;
        var r = vm.BuildResponses();
        Assert.Equal(CalibrationSource.Buttons, r.Q1Source);
        Assert.Equal(CalibrationSource.Buttons, r.Q2Source);
        Assert.Equal(CalibrationSource.Buttons, r.Q3Source);
        Assert.Equal(CalibrationSource.Buttons, r.Q4Source);
        Assert.Equal(CalibrationSource.Buttons, r.Q5Source);
    }

    // ── Completion / cancel events ──────────────────────────────────────────

    [Fact]
    public void Complete_OnLastCard_FiresCompletedEventWithResponses()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q3Intensity = 5;
        for (int i = 0; i < 4; i++) vm.NextCommand.Execute(null);

        CalibrationResponses? captured = null;
        vm.Completed += (_, r) => captured = r;
        vm.CompleteCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(5, captured!.Q3Intensity);
    }

    [Fact]
    public void Cancel_AnyTime_FiresCancelledEventWithSnapshot()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q1Segment = UncomfortableSegment.Middle;

        CalibrationResponses? captured = null;
        vm.Cancelled += (_, r) => captured = r;
        vm.CancelCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(UncomfortableSegment.Middle, captured!.Q1Segment);
        // Q3 unanswered → caller's CalibrationProcessor will classify as Abandoned.
        Assert.Null(captured.Q3Intensity);
    }

    [Fact]
    public void Cancel_AfterQ3Set_LeavesQ3IntensityIntactForProcessor()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q3Intensity = 4;

        CalibrationResponses? captured = null;
        vm.Cancelled += (_, r) => captured = r;
        vm.CancelCommand.Execute(null);

        Assert.Equal(4, captured!.Q3Intensity);
        // Caller's CalibrationProcessor will classify as Partial (Q3 set,
        // others unset).
    }
}
