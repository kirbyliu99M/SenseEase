using System;
using System.IO;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 (Phase 7d + 7f + 7g): integration tests for HabituationOverrideApplicator.
/// Exercises the propose→accept→engine-write path, the defensive-intercept
/// blocker (7f), the decline / expire bookkeeping, and the IChatbotActionLog
/// audit trail.
/// </summary>
public class HabituationOverrideApplicatorTests
{
    // ── Test fixture helpers ─────────────────────────────────────────────────

    private sealed class FakeProfileStore : IHabituationProfileStore
    {
        private HabituationProfileSnapshot? _current;
        public FakeProfileStore(HabituationProfileSnapshot? initial = null) => _current = initial;
        public HabituationProfileSnapshot? Load() => _current;
        public void Save(HabituationProfileSnapshot snapshot) => _current = snapshot;
    }

    private static (HabituationOverrideApplicator app,
                    MockInferenceEngine engine,
                    FakeProfileStore profileStore,
                    InMemoryEventLogStore eventLog,
                    InMemoryChatbotActionLog actionLog)
        Build(HabituationProfileSnapshot? initialSnapshot = null)
    {
        var profileStore = new FakeProfileStore(initialSnapshot);
        var eventLog = new InMemoryEventLogStore();
        var actionLog = new InMemoryChatbotActionLog();
        var engine = new MockInferenceEngine(
            adjustmentLog: null,
            habituationStore: null,
            profileStore: profileStore,
            eventLog: eventLog);
        var applicator = new HabituationOverrideApplicator(engine, profileStore, eventLog, actionLog);
        return (applicator, engine, profileStore, eventLog, actionLog);
    }

    private static ProfileOverrideProposal MakeProposal(
        OverrideIntent intent,
        object value,
        DateTime? expires = null)
    {
        return new ProfileOverrideProposal(
            Intent: intent,
            TargetField: intent.ToString().ToLowerInvariant(),
            ProposedValue: value,
            Rationale: "test rationale",
            ExpiresAtUtc: expires ?? DateTime.UtcNow.AddDays(2));
    }

    private static HabituationProfileSnapshot MakeSnapshot(
        string profile = "TYPICAL",
        double scale = 1.0,
        int strikeLimit = 2,
        MedicalAdvisory? advisory = null)
    {
        return new HabituationProfileSnapshot
        {
            SchemaVersion = 2,
            Profile = profile,
            ProfileSource = "calibration_demo",
            PersonalPressureScale = scale,
            SevereStrikeLimit = strikeLimit,
            MedicalAdvisoryShown = advisory,
        };
    }

    // ── ApplyAccepted happy paths ────────────────────────────────────────────

    [Fact]
    public void ApplyAccepted_AdjustSensitivity_WritesEngine_LogsAction()
    {
        var (app, engine, _, eventLog, actionLog) = Build(MakeSnapshot(scale: 1.0));
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.IsSuccess);
        Assert.False(result.BlockedBySafety);
        Assert.Single(actionLog.Recent());
        Assert.Equal(ChatbotActionKind.Accepted, actionLog.Recent()[0].Kind);
        // Engine recorded the override event in its own log.
        Assert.Contains(eventLog.Recent(),
                        e => e.Category == "Habituation.Profile"
                          && e.Message.Contains("chatbot override applied"));
    }

    [Fact]
    public void ApplyAccepted_SwitchProfile_UpdatesEngineProfile()
    {
        var (app, engine, _, _, actionLog) = Build(MakeSnapshot(profile: "RESILIENT"));
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "SENSITIVE");

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.IsSuccess);
        Assert.Single(actionLog.Recent());
    }

    [Fact]
    public void ApplyAccepted_AdjustStrikeLimit_SetsOverride()
    {
        var (app, engine, _, _, _) = Build(MakeSnapshot(strikeLimit: 2));
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)3);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, engine.HabituationSevereStrikeLimitOverride);
    }

    // ── Phase 7f: defensive intercept blocks ─────────────────────────────────

    [Fact]
    public void ApplyAccepted_VestibularUser_RelaxationProposal_BlockedBySafety()
    {
        var snapshot = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.Vestibular);
        var (app, engine, _, eventLog, actionLog) = Build(snapshot);
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8); // relaxation

        var result = app.ApplyAccepted(proposal);

        Assert.False(result.IsSuccess);
        Assert.True(result.BlockedBySafety);
        Assert.Contains("vestibular", result.Reason!);
        // Action logged as Intercepted, not Accepted.
        Assert.Equal(ChatbotActionKind.Intercepted, actionLog.Recent()[0].Kind);
        // SafetyIntercept event surfaces in the event log.
        Assert.Contains(eventLog.Recent(),
                        e => e.Category == "Habituation.SafetyIntercept");
    }

    [Fact]
    public void ApplyAccepted_HighRiskCombo_SignificantRelaxation_Blocked()
    {
        var snapshot = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.HighRiskCombo);
        var (app, _, _, _, actionLog) = Build(snapshot);
        // 20% drop in scale = significant relaxation under threshold 0.15
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.BlockedBySafety);
        Assert.Contains("high-risk-combo", result.Reason!);
    }

    [Fact]
    public void ApplyAccepted_HighRiskCombo_SmallRelaxation_Allowed()
    {
        var snapshot = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.HighRiskCombo);
        var (app, _, _, _, _) = Build(snapshot);
        // 5% drop in scale = below threshold 0.15
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.95);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ApplyAccepted_VestibularHardCap_StrikeLimit1_BlocksAdjust()
    {
        var snapshot = MakeSnapshot(strikeLimit: 1, advisory: MedicalAdvisory.Vestibular);
        var (app, _, _, _, _) = Build(snapshot);
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)3);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.BlockedBySafety);
        Assert.Contains("hard-set", result.Reason!);
    }

    [Fact]
    public void ApplyAccepted_NoSnapshot_NoInterceptApplied()
    {
        var (app, _, _, _, _) = Build(initialSnapshot: null);
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.IsSuccess);
    }

    // ── RecordDeclined / RecordExpired ───────────────────────────────────────

    [Fact]
    public void RecordDeclined_LogsAction_MostRecentDeclineFor_SurfacesIt()
    {
        var (app, _, _, _, actionLog) = Build();
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);

        var before = DateTime.UtcNow;
        app.RecordDeclined(proposal);
        var after = DateTime.UtcNow;

        var when = actionLog.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity);
        Assert.NotNull(when);
        Assert.InRange(when!.Value, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(ChatbotActionKind.Declined, actionLog.Recent()[0].Kind);
    }

    [Fact]
    public void RecordExpired_LogsAction()
    {
        var (app, _, _, _, actionLog) = Build();
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "TYPICAL");

        app.RecordExpired(proposal);

        Assert.Equal(ChatbotActionKind.Expired, actionLog.Recent()[0].Kind);
    }

    // ── Static CheckSafetyIntercepts (Phase 7f predicate) ────────────────────

    [Fact]
    public void CheckSafetyIntercepts_NullSnapshot_Passes()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, null);
        Assert.False(result.Blocked);
    }

    [Fact]
    public void CheckSafetyIntercepts_NoAdvisory_Passes()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8);
        var snapshot = MakeSnapshot(scale: 1.0, advisory: null);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);
        Assert.False(result.Blocked);
    }
}
