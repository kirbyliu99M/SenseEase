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
    public void ApplyAccepted_SwitchProfile_NoOps_ProfileUnchanged()
    {
        // Reading A (2026-06-06): the chatbot can no longer switch the habituation
        // profile — it is set ONLY at calibration. SwitchProfile is a defensive
        // no-op at the engine (ApplyChatbotOverride returns applied=false), so
        // ApplyAccepted does NOT succeed and the engine profile is left untouched.
        var (app, engine, _, _, actionLog) = Build(MakeSnapshot(profile: "RESILIENT"));
        var before = engine.HabituationProfile;
        var beforeSource = engine.HabituationProfileSource;
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "SENSITIVE");

        var result = app.ApplyAccepted(proposal);

        // No-op: not a success, and the engine profile is unchanged.
        Assert.False(result.IsSuccess);
        Assert.Equal(before, engine.HabituationProfile);
        Assert.Equal(beforeSource, engine.HabituationProfileSource);
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
    // (Vestibular-removal: the vestibular intercept facts were stripped; the HighRiskCombo
    //  intercept facts below are the surviving advisory-block coverage. The interceptor's
    //  vestibular rules are kept as dead-but-protective — migration nulls the advisory.)

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
    public void ApplyAccepted_NoAdvisory_AppliesProposal()
    {
        // H15: a non-null snapshot with no advisory still applies (the no-advisory
        // pass-through path). The null-snapshot case now fails CLOSED — see below.
        var (app, _, _, _, _) = Build(initialSnapshot: MakeSnapshot(advisory: null));
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);

        var result = app.ApplyAccepted(proposal);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ApplyAccepted_NullSnapshot_BlockedFailClosed_H15()
    {
        // H15: a corrupt / missing profile (SafeLoadSnapshot → null) must BLOCK the
        // apply, not fail open. The block path never calls ApplyChatbotOverride, so
        // the engine is not mutated; the Card sees the "please reload" reason.
        var (app, _, _, _, actionLog) = Build(initialSnapshot: null);
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);

        var result = app.ApplyAccepted(proposal);

        Assert.False(result.IsSuccess);
        Assert.True(result.BlockedBySafety);
        Assert.Contains("verify safety", result.Reason);
        Assert.Equal(ChatbotActionKind.Intercepted, actionLog.Recent()[0].Kind);
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
    public void CheckSafetyIntercepts_NullSnapshot_Blocks_H15()
    {
        // H15: fail closed when safety state can't be verified.
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 1.1);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, null);
        Assert.True(result.Blocked);
        Assert.Contains("verify safety", result.Reason);
    }

    [Fact]
    public void CheckSafetyIntercepts_NoAdvisory_Passes()
    {
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8);
        var snapshot = MakeSnapshot(scale: 1.0, advisory: null);
        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);
        Assert.False(result.Blocked);
    }

    // ── C6: per-rule × per-intent safety-intercept matrix ────────────────────
    //
    // CheckSafetyIntercepts has THREE intercept rules, evaluated most-specific
    // first (see HabituationOverrideApplicator.cs):
    //   Rule 1 — AdjustStrikeLimit + SevereStrikeLimit == 1 + Vestibular advisory
    //            → "strike limit was hard-set …; chatbot cannot override".
    //            GUARD requires Intent == AdjustStrikeLimit, so this rule is
    //            logically UNREACHABLE for the other two intents — those cells
    //            are [Fact(Skip=…)] below.
    //   Rule 2 — Vestibular advisory + ANY relaxation proposal (all three intents
    //            can relax) → "vestibular advisory active; … manual Settings".
    //   Rule 3 — HighRiskCombo advisory + SIGNIFICANT relaxation (≥ 15 % relative,
    //            and a profile switch counts as always-significant) →
    //            "high-risk-combo advisory; small adjustments only …".
    //
    // The matrix is exercised against the static predicate directly so each cell
    // is a pure rule×intent assertion with no engine state in the loop. Every
    // expected value is derived from the real reason strings / relaxation
    // definitions above — substring invariants, not brittle magic numbers.

    // ── Rule 1 row (vestibular hard-set strike-limit == 1) ───────────────────

    [Fact(Skip = "Rule 1 guard requires Intent == AdjustStrikeLimit; AdjustSensitivity can never reach the hard-set rule (it falls through to Rule 2). Logically unreachable cell.")]
    public void Matrix_Rule1_AdjustSensitivity_Unreachable() { }

    [Fact(Skip = "Rule 1 guard requires Intent == AdjustStrikeLimit; SwitchProfile can never reach the hard-set rule (it falls through to Rule 2). Logically unreachable cell.")]
    public void Matrix_Rule1_SwitchProfile_Unreachable() { }

    // (Vestibular-removal: the Rule 1 (vestibular hard-set strike-limit) and Rule 2 (vestibular
    //  relaxation) matrix facts were stripped. Those interceptor rules are kept as
    //  dead-but-protective; the Rule 3 (HighRiskCombo) matrix below is the surviving coverage.)

    // ── Rule 3 row (high-risk-combo + significant relaxation) ────────────────

    [Fact]
    public void Matrix_Rule3_AdjustSensitivity_BlocksWithHighRiskReason()
    {
        // 20 % drop (1.0 → 0.8) ≥ 15 % threshold ⇒ significant relaxation.
        var snapshot = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.HighRiskCombo);
        var proposal = MakeProposal(OverrideIntent.AdjustSensitivity, 0.8);

        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);

        Assert.True(result.Blocked);
        Assert.Contains("high-risk-combo", result.Reason!);
    }

    [Fact]
    public void Matrix_Rule3_SwitchProfile_ProfileUnchanged()
    {
        // Reading A (2026-06-06): SwitchProfile no longer applies at the engine
        // (profile is calibration-only), so this is no longer a meaningful Rule-3
        // BLOCK test — the proposal can never mutate the profile regardless of the
        // high-risk-combo advisory. Assert the engine profile is UNCHANGED after the
        // attempt to keep coverage of the no-op at the apply path.
        var (app, engine, _, _, _) = Build(
            MakeSnapshot(profile: "TYPICAL", advisory: MedicalAdvisory.HighRiskCombo));
        var before = engine.HabituationProfile;
        var proposal = MakeProposal(OverrideIntent.SwitchProfile, "RESILIENT");

        var result = app.ApplyAccepted(proposal);

        Assert.False(result.IsSuccess);
        Assert.Equal(before, engine.HabituationProfile);
    }

    [Fact]
    public void Matrix_Rule3_AdjustStrikeLimit_BlocksWithHighRiskReason()
    {
        // 2 → 4 is a (4-2)/2 = 100 % increase ≥ 15 % threshold ⇒ significant.
        var snapshot = MakeSnapshot(strikeLimit: 2, advisory: MedicalAdvisory.HighRiskCombo);
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)4);

        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);

        Assert.True(result.Blocked);
        Assert.Contains("high-risk-combo", result.Reason!);
    }

    // ── Rule 3 boundary: SMALL high-risk relaxation passes (below threshold) ──

    [Fact]
    public void Matrix_Rule3_AdjustStrikeLimit_NonRelaxation_Passes()
    {
        // IsSignificantRelaxation short-circuits to false for any non-relaxation.
        // A strike limit equal to the current one (3 == 3) is not a relaxation
        // (the predicate needs limit > current), so Rule 3 does not fire even
        // under a high-risk-combo advisory.
        var snapshot = MakeSnapshot(strikeLimit: 3, advisory: MedicalAdvisory.HighRiskCombo);
        var proposal = MakeProposal(OverrideIntent.AdjustStrikeLimit, (long)3); // no change

        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);

        Assert.False(result.Blocked);
    }

    // (Vestibular-removal: the Rule1-wins-over-Rule2 precedence fact was vestibular-only and
    //  was removed; the interceptor's precedence ordering is kept as dead-but-protective code.)
}
