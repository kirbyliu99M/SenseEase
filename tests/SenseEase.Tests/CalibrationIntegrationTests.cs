using System.Collections.ObjectModel;
using SenseEase.App.ViewModels;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Stage 3g — end-to-end integration tests for the calibration card flow.
///
/// Tests are written at the VM + engine layer (NOT WPF) so they run on the
/// CI without an STA dispatcher. We bypass MainViewModel entirely and drive
/// the same code path it uses: <see cref="CalibrationApplicator.Apply"/>
/// against a fresh <see cref="MockInferenceEngine"/> + a bare
/// <see cref="ObservableCollection{ChatMessageViewModel}"/> standing in for
/// <c>ChatbotViewModel.Messages</c>.
///
/// The Q5 mutual-exclusivity matrix is tested at the
/// <see cref="CalibrationCardViewModel"/> level only — there is no UI step
/// here, just direct property assignment. Cases already covered by
/// <see cref="CalibrationCardViewModelTests"/> are not duplicated.
/// </summary>
public class CalibrationIntegrationTests
{
    // Sensor stub used by every test. PeakPressureAtSec=0 so Q1-alignment
    // never claims "high confidence" purely on a coincidence.
    private static readonly CalibrationSensorData Sensor =
        new(PeakPressure: 60.0, MaskActivations: 1, PeakPressureAtSec: 0);

    // ── (a) End-to-end happy path ───────────────────────────────────────────

    [Fact]
    public void EndToEnd_HappyPath_AppliesProfileAndPostsSummary()
    {
        // Simulate the 5-card VM after a user clicked through Q1..Q5.
        var card = new CalibrationCardViewModel();
        card.Q1Segment   = UncomfortableSegment.Middle;
        card.Q2Dizziness = true;
        card.Q2Nausea    = true;
        card.Q3Intensity = 3;
        card.Q4Comparison = BaselineComparison.Similar;
        card.Q5None       = true;

        var engine   = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();

        // Drive Completed in-place rather than animating the Next clicks —
        // navigation is already covered by CalibrationCardViewModelTests.
        var result = CalibrationApplicator.Apply(card.BuildResponses(), Sensor, engine, messages);

        Assert.Equal(CalibrationCompleteness.Complete, result.Completeness);
        // Q3=3 + Q4=Similar → TYPICAL (per CalibrationProcessor §5 step 1).
        Assert.Equal(HabituationProfile.Typical, engine.HabituationProfile);
        Assert.Equal("calibration_demo", engine.HabituationProfileSource);

        Assert.Single(messages);
        string last = messages[messages.Count - 1].Content;
        Assert.Contains("Calibration 完成", last);
        Assert.Contains("3/5", last);
        Assert.Contains("頭暈", last); // Dizziness label
        Assert.Contains("想吐", last); // Nausea label
        Assert.Contains("Typical", last);
    }

    // ── (b) Cancel paths ────────────────────────────────────────────────────

    [Fact]
    public void Cancel_BeforeQ3_NoEngineChange_NoMessages()
    {
        var card = new CalibrationCardViewModel();
        card.Q1Segment = UncomfortableSegment.Start;
        // Q3 deliberately left null — Abandoned classification.

        var engine   = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();
        double triggerBefore = engine.HabituationEffectiveTrigger;
        var profileBefore    = engine.HabituationProfile;

        var result = CalibrationApplicator.Apply(card.BuildResponses(), Sensor, engine, messages);

        Assert.Equal(CalibrationCompleteness.Abandoned, result.Completeness);
        Assert.Empty(messages);
        // Profile + trigger untouched.
        Assert.Equal(profileBefore, engine.HabituationProfile);
        Assert.Equal(triggerBefore, engine.HabituationEffectiveTrigger, precision: 2);
        Assert.Null(engine.LastCalibrationResult);
    }

    [Fact]
    public void Cancel_AfterQ3Only_AppliesAsPartial_PostsPartialSummary()
    {
        var card = new CalibrationCardViewModel();
        card.Q3Intensity = 4; // Q3 set, everything else null → Partial.

        var engine   = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();

        var result = CalibrationApplicator.Apply(card.BuildResponses(), Sensor, engine, messages);

        Assert.Equal(CalibrationCompleteness.Partial, result.Completeness);
        Assert.NotNull(engine.LastCalibrationResult);
        // Q3=4 → SENSITIVE per §5 step 1.
        Assert.Equal(HabituationProfile.Sensitive, engine.HabituationProfile);

        Assert.Single(messages);
        string last = messages[messages.Count - 1].Content;
        Assert.Contains("Calibration 已部分完成", last);
        Assert.Contains("4/5", last);
        Assert.Contains("未指定", last); // Q2 untouched → "unspecified"
    }

    // ── (c) Q5 mutual-exclusivity matrix (VM-level) ─────────────────────────
    // CalibrationCardViewModelTests already covers:
    //   - Q5None clears MotionSickness + Skipped       (PickingNone_ClearsAllRisksAndSkipped)
    //   - Q5Skipped clears Q5None                       (PickingSkipped_ClearsAllIncludingNone)
    //   - Q5Migraine after Q5Skipped clears Skipped+None (PickingARisk_ClearsNoneAndSkipped)
    // Add the four cases listed in the brief that are NOT yet covered:
    //   1. Q5None + Q5MotionSickness=true → None cleared, MS true
    //   2. Q5None + Q5Skipped=true → None cleared, Skipped true
    //   3. Q5Skipped + Q5MotionSickness=true → Skipped cleared, MS true
    //   4. Q5MotionSickness + Q5None=true → MS cleared, None true
    //   5. Q5MotionSickness + Q5Skipped=true → MS cleared, Skipped true
    //   6. {MS, Migraine} + Q5None=true → both cleared, None true

    [Fact]
    public void Q5Matrix_NoneThenMotionSickness_NoneCleared()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5None = true;
        vm.Q5MotionSickness = true;
        Assert.False(vm.Q5None);
        Assert.True(vm.Q5MotionSickness);
    }

    [Fact]
    public void Q5Matrix_NoneThenSkipped_NoneCleared()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5None = true;
        vm.Q5Skipped = true;
        Assert.False(vm.Q5None);
        Assert.True(vm.Q5Skipped);
    }

    [Fact]
    public void Q5Matrix_SkippedThenMotionSickness_SkippedCleared()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5Skipped = true;
        vm.Q5MotionSickness = true;
        Assert.False(vm.Q5Skipped);
        Assert.False(vm.Q5None);
        Assert.True(vm.Q5MotionSickness);
    }

    [Fact]
    public void Q5Matrix_MotionSicknessThenNone_MotionSicknessCleared()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5MotionSickness = true;
        vm.Q5None = true;
        Assert.False(vm.Q5MotionSickness);
        Assert.True(vm.Q5None);
    }

    [Fact]
    public void Q5Matrix_MotionSicknessThenSkipped_MotionSicknessCleared()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5MotionSickness = true;
        vm.Q5Skipped = true;
        Assert.False(vm.Q5MotionSickness);
        Assert.True(vm.Q5Skipped);
    }

    [Fact]
    public void Q5Matrix_MotionSicknessAndMigraineThenNone_BothCleared()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q5MotionSickness = true;
        vm.Q5Migraine       = true;
        vm.Q5None = true;
        Assert.False(vm.Q5MotionSickness);
        Assert.False(vm.Q5Migraine);
        Assert.True(vm.Q5None);
    }

    // ── (d) Q3 set → clear → set different value → completes successfully ───

    [Fact]
    public void Q3_SetClearReset_DifferentValue_CompletesSuccessfully()
    {
        var vm = new CalibrationCardViewModel();
        vm.Q3Intensity = 2;
        Assert.True(vm.CanAdvance is true || vm.CurrentCardIndex != 2);

        vm.Q3Intensity = null;
        // Advance to card 3 to observe the mandatory gate.
        vm.NextCommand.Execute(null); // card 2
        vm.NextCommand.Execute(null); // card 3
        Assert.Equal(2, vm.CurrentCardIndex);
        Assert.False(vm.CanAdvance);

        vm.Q3Intensity = 5;
        Assert.True(vm.CanAdvance);
        vm.NextCommand.Execute(null); // -> card 4
        vm.NextCommand.Execute(null); // -> card 5
        Assert.True(vm.IsLastCard);
        Assert.True(vm.CompleteCommand.CanExecute(null));

        CalibrationResponses? captured = null;
        vm.Completed += (_, r) => captured = r;
        vm.CompleteCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(5, captured!.Q3Intensity);
    }

    // ── (e) Vestibular branch → SENSITIVE + override 50 + strike limit 1 ────

    [Fact]
    public void Vestibular_FullCalibration_SetsSensitiveProfileOverrideAndStrikeLimit()
    {
        var responses = new CalibrationResponses
        {
            Q1Segment    = UncomfortableSegment.Middle,
            Q2Symptoms   = new[] { SymptomType.Dizziness, SymptomType.Imbalance },
            Q3Intensity  = 4,
            Q4Comparison = BaselineComparison.Worse,
            Q5RiskFactors = new[] { RiskFactor.VestibularDiagnosed },
        };
        var engine   = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();

        var result = CalibrationApplicator.Apply(responses, Sensor, engine, messages);

        // Vestibular branch hard-locks per CalibrationProcessor:
        //   profile -> SENSITIVE, override 50, strike-limit 1, advisory "vestibular".
        Assert.Equal(HabituationProfile.Sensitive, engine.HabituationProfile);
        Assert.Equal(50.0, engine.HabituationEffectiveTrigger, precision: 2);
        Assert.Equal(1, engine.HabituationSevereStrikeLimitOverride);
        Assert.Equal("vestibular", result.MedicalAdvisory);

        // Two messages: summary + vestibular advisory.
        Assert.Equal(2, messages.Count);
        Assert.Contains("Calibration 完成", messages[0].Content);
        Assert.Contains("醫療提醒", messages[1].Content);
        Assert.Contains("vestibular condition", messages[1].Content);
    }

    // ── (f) High-risk combo (Q3=5 + ≥2 risk factors) → advisory posted ──────

    [Fact]
    public void HighRiskCombo_Q3FivePlusTwoRiskFactors_PostsMedicalAdvisory()
    {
        var responses = new CalibrationResponses
        {
            Q1Segment    = UncomfortableSegment.End,
            Q2Symptoms   = new[] { SymptomType.Nausea, SymptomType.Headache },
            Q3Intensity  = 5,
            Q4Comparison = BaselineComparison.Worse,
            // Two non-vestibular risk factors → high_risk_combo branch.
            Q5RiskFactors = new[] { RiskFactor.MotionSickness, RiskFactor.Migraine },
        };
        var engine   = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();

        var result = CalibrationApplicator.Apply(responses, Sensor, engine, messages);

        Assert.Equal("high_risk_combo", result.MedicalAdvisory);
        // riskCount >= 2 escalates one level. Base profile for Q3=5 is already
        // SENSITIVE, so it stays SENSITIVE (top of the escalation ladder).
        Assert.Equal(HabituationProfile.Sensitive, engine.HabituationProfile);
        // Advisory bubble appended after the summary.
        Assert.Equal(2, messages.Count);
        Assert.Contains("高強度", messages[1].Content);
        Assert.Contains("high intensity", messages[1].Content);
    }

    // ── (g) Symptom-list formatting edge case ───────────────────────────────

    [Fact]
    public void Summary_Q2Unanswered_RendersAsUnspecified()
    {
        var responses = new CalibrationResponses
        {
            Q1Segment    = UncomfortableSegment.None,
            // Q2Symptoms left null → "未指定"
            Q3Intensity  = 2,
            Q4Comparison = BaselineComparison.Similar,
            Q5RiskFactors = new[] { RiskFactor.None },
        };
        var engine   = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();

        CalibrationApplicator.Apply(responses, Sensor, engine, messages);
        Assert.Single(messages);
        Assert.Contains("未指定", messages[0].Content);
    }
}
