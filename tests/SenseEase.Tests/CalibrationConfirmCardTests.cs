using System.Collections.ObjectModel;
using SenseEase.App.ViewModels;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// H11 — the calibration confirmation gate. A completed calibration must NOT mutate
/// the engine until the user accepts the confirmation card; declining keeps the
/// current settings unchanged. These tests pin that contract at the testable layer
/// (CalibrationConfirmCardViewModel + the CalibrationApplicator compute/apply split),
/// which is exactly how MainViewModel wires the card.
/// </summary>
public class CalibrationConfirmCardTests
{
    // A complete, non-trivial calibration: vestibular-diagnosed → a profile that is
    // NOT the RESILIENT default, so "engine changed" is observable via CurrentProfile.
    private static CalibrationResponses CompleteResponses() => new()
    {
        Q1Segment     = UncomfortableSegment.Middle,
        Q2Symptoms    = new[] { SymptomType.Dizziness, SymptomType.Nausea },
        Q3Intensity   = 5,
        Q4Comparison  = BaselineComparison.Worse,
        Q5RiskFactors = new[] { RiskFactor.VestibularDiagnosed },
    };

    private static readonly CalibrationSensorData Sensor = new(80.0, 3, 12.0);

    [Fact]
    public void H11_EngineNotMutated_UntilConfirmAccept()
    {
        var engine = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();
        var responses = CompleteResponses();

        // Pure compute — no engine mutation.
        CalibrationResult result = CalibrationProcessor.Process(responses, Sensor);
        Assert.Equal(CalibrationCompleteness.Complete, result.Completeness);
        Assert.NotEqual(HabituationProfile.Resilient, result.Profile); // the apply WOULD change the profile

        var confirm = new CalibrationConfirmCardViewModel(responses, result);
        bool accepted = false;
        confirm.Accepted += (_, _) =>
        {
            accepted = true;
            CalibrationApplicator.ApplyComputed(result, responses, engine, messages);
        };

        // Card created, not yet accepted → engine still at the RESILIENT default.
        Assert.Equal(HabituationProfile.Resilient, engine.HabituationProfile);
        Assert.Empty(messages);

        confirm.AcceptCommand.Execute(null);

        // Now — and only now — the engine reflects the calibration.
        Assert.True(accepted);
        Assert.Equal(result.Profile, engine.HabituationProfile);
        Assert.NotEmpty(messages); // summary (+ advisory) posted on apply
    }

    [Fact]
    public void H11_Decline_LeavesEngineAndTranscriptUntouched()
    {
        var engine = new MockInferenceEngine();
        var messages = new ObservableCollection<ChatMessageViewModel>();
        var responses = CompleteResponses();
        CalibrationResult result = CalibrationProcessor.Process(responses, Sensor);

        var confirm = new CalibrationConfirmCardViewModel(responses, result);
        bool declined = false;
        confirm.Accepted += (_, _) => CalibrationApplicator.ApplyComputed(result, responses, engine, messages);
        confirm.Declined += (_, _) => declined = true;

        confirm.DeclineCommand.Execute(null);

        Assert.True(declined);
        Assert.Equal(HabituationProfile.Resilient, engine.HabituationProfile); // never applied
        Assert.Empty(messages);                                            // no summary posted
    }

    [Fact]
    public void H11_ConfirmCard_RendersQ1ToQ5Recap_AndOutcomePreview()
    {
        var responses = CompleteResponses();
        CalibrationResult result = CalibrationProcessor.Process(responses, Sensor);

        var confirm = new CalibrationConfirmCardViewModel(responses, result);

        Assert.Equal(5, confirm.RecapLines.Count);                 // Q1–Q5, one line each
        Assert.Contains(confirm.RecapLines, l => l.Contains("5/5")); // Q3 intensity, language-agnostic
        Assert.Contains(result.Profile.ToString().ToUpperInvariant(), confirm.OutcomeText);
        Assert.False(string.IsNullOrWhiteSpace(confirm.HeaderText));
        Assert.False(string.IsNullOrWhiteSpace(confirm.AcceptText));
        Assert.False(string.IsNullOrWhiteSpace(confirm.DeclineText));
    }
}
