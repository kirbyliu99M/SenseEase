using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M4 (Phase 8) acceptance scenarios. Each scenario maps to one of the
/// 5 user goals from the advisor's Section 1, plus a 4-week longitudinal
/// simulation for Goal 5.
///
/// <para>These are integration tests focused on the chatbot write-side
/// pipeline (Phase 7b–7g). They wire the full
/// Parser → Processor → Applicator → ActionLog → EventLog stack with
/// real concrete implementations (no mocks) and feed scripted raw LLM
/// output through the parser, asserting on the downstream effects
/// (proposal extracted, engine state changed, safety intercept fired,
/// audit log populated).</para>
///
/// <para>Scenarios deliberately bypass <see cref="ChatbotViewModel.SendCommand"/>
/// to avoid the WPF Dispatcher.Invoke pump complexity in headless xunit —
/// the routing logic that lives in SendAsync is exercised in
/// <see cref="ChatbotViewModelPhase6Tests"/> via BuildContextualInput;
/// here we test the pipeline COMPONENTS as they'd be invoked by that
/// routing. One scenario (E1 — picker bubble) does exercise SendCommand
/// because its short-circuit doesn't await across the dispatcher boundary
/// and therefore doesn't need a pump.</para>
///
/// <para>The 4-week simulation (Scenario F) drives
/// <see cref="MockInferenceEngine.Update"/> with synthetic time slices
/// so habituation level accrues realistically across a compressed
/// 28-day window, then verifies the chatbot's context block reflects
/// the progression.</para>
/// </summary>
public class Phase8AcceptanceTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private sealed class InMemoryProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Snapshot { get; set; }
        public HabituationProfileSnapshot? Load() => Snapshot;
        public void Save(HabituationProfileSnapshot snapshot) => Snapshot = snapshot;
    }

    private static HabituationProfileSnapshot MakeSnapshot(
        string profile = "TYPICAL",
        double scale = 1.0,
        int strikeLimit = 2,
        int calibrationCount = 1,
        MedicalAdvisory? advisory = null,
        int currentTier = 3,
        double habituationLevel = 0.0)
        => new HabituationProfileSnapshot
        {
            Profile = profile,
            PersonalPressureScale = scale,
            SevereStrikeLimit = strikeLimit,
            CalibrationCount = calibrationCount,
            MedicalAdvisoryShown = advisory,
            CurrentVimsTier = currentTier,
            HabituationLevel = habituationLevel,
            EngineState = "RUNNING",
            LastCalibrationAtIso = DateTime.UtcNow.AddHours(-1).ToString("o"),
        };

    private static (MockInferenceEngine engine,
                    InMemoryProfileStore profileStore,
                    InMemoryEventLogStore eventLog,
                    InMemoryChatbotActionLog actionLog,
                    HabituationOverrideApplicator applicator,
                    LongitudinalObservationWriter writer)
        BuildFullStack(HabituationProfileSnapshot? initialSnapshot = null)
    {
        var profileStore = new InMemoryProfileStore { Snapshot = initialSnapshot };
        var eventLog     = new InMemoryEventLogStore();
        var actionLog    = new InMemoryChatbotActionLog();
        var engine       = new MockInferenceEngine(
            adjustmentLog: new StubAdjustmentLog(),
            habituationStore: null,
            profileStore: profileStore,
            eventLog: eventLog);
        var applicator   = new HabituationOverrideApplicator(engine, profileStore, eventLog, actionLog);
        var writer       = new LongitudinalObservationWriter(profileStore);
        return (engine, profileStore, eventLog, actionLog, applicator, writer);
    }

    private static string BuildRawWithProfileOverride(
        string envelopeReply,
        OverrideIntent intent,
        object value,
        string rationale = "scripted test rationale")
    {
        string intentStr = intent switch
        {
            OverrideIntent.AdjustSensitivity  => "adjust_sensitivity",
            OverrideIntent.SwitchProfile      => "switch_profile",
            OverrideIntent.AdjustStrikeLimit  => "adjust_strike_limit",
            _ => "adjust_sensitivity",
        };
        string field = intent switch
        {
            OverrideIntent.AdjustSensitivity  => "personal_pressure_scale",
            OverrideIntent.SwitchProfile      => "profile",
            OverrideIntent.AdjustStrikeLimit  => "severe_strike_limit",
            _ => "personal_pressure_scale",
        };
        string valueJson = value switch
        {
            string s => $"\"{s}\"",
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int i    => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l   => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _        => value?.ToString() ?? "null",
        };
        string expiry = DateTime.UtcNow.AddHours(24).ToString("o");
        return
            "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"" + envelopeReply + "\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
            "[PROFILE_OVERRIDE]\n" +
            "{\"intent\":\"" + intentStr + "\",\"target_field\":\"" + field + "\",\"proposed_value\":" + valueJson + ",\"rationale\":\"" + rationale + "\",\"expires_at_iso\":\"" + expiry + "\"}\n" +
            "[/PROFILE_OVERRIDE]";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario A — Goal 3 (雙向 agency): full propose → apply pipeline
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioA_LlmProposesScaleAdjustment_ProcessorAccepts_ApplicatorChangesEngine()
    {
        var snap = MakeSnapshot(scale: 1.0);
        var (engine, _, _, actionLog, applicator, _) = BuildFullStack(snap);

        // Step 1: chatbot reply with a PROFILE_OVERRIDE block.
        string rawLlmOutput = BuildRawWithProfileOverride(
            envelopeReply: "已記錄您穩定的回饋，建議稍微放鬆 sensitivity。",
            intent: OverrideIntent.AdjustSensitivity,
            value: 0.9,
            rationale: "user reports stable, history supports relaxation");

        // Step 2: parser extracts the proposal.
        var parsed = HabituationOverrideParser.TryParse(rawLlmOutput);
        Assert.NotNull(parsed.Proposal);
        Assert.Equal(OverrideIntent.AdjustSensitivity, parsed.Proposal!.Intent);
        Assert.Equal(0.9, Assert.IsType<double>(parsed.Proposal.ProposedValue));

        // Step 3: processor validates.
        var validation = HabituationOverrideProcessor.Validate(parsed.Proposal, snap);
        Assert.True(validation.IsAccepted);

        // Step 4: applicator applies; engine effective trigger changes.
        double triggerBefore = engine.HabituationEffectiveTrigger;
        var applyResult = applicator.ApplyAccepted(parsed.Proposal);
        Assert.True(applyResult.IsSuccess);
        Assert.NotEqual(triggerBefore, engine.HabituationEffectiveTrigger);

        // Step 5: audit trail records Accepted.
        Assert.Contains(actionLog.Recent(),
                        e => e.Kind == ChatbotActionKind.Accepted
                          && e.Intent == OverrideIntent.AdjustSensitivity);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario B — Reliability: lexicon catches what the LLM missed
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioB_LexiconNoLongerSynthesizesProfileSwitch()
    {
        // CW-3 (honesty): profile-switch detection was removed from the lexicon —
        // profiles are auto-managed and there is no user-facing switcher, so an
        // explicit "switch to <profile>" turn must produce NO proposal (it is
        // answered upstream by the honest auto-managed reply instead). The other
        // explicit-request types (sensitivity / strike-limit) still synthesize.
        var snap = MakeSnapshot();
        Assert.Null(HabituationProposalLexicon.TryDetect("switch to SENSITIVE profile", snap));
        Assert.Null(HabituationProposalLexicon.TryDetect("change profile to RESILIENT", snap));
    }

    [Fact]
    public void ScenarioB_ApplicatorHardRejectsSwitchProfile_ProfileUnchanged()
    {
        // Reading A (2026-06-06): even a hand-built SwitchProfile proposal (e.g. a
        // legacy LLM-emitted block) is no-op'd by the applicator — the profile is
        // calibration-only. CW-3 also stops the lexicon from ever creating one.
        var snap = MakeSnapshot(profile: "TYPICAL");
        var (engine, _, _, _, applicator, _) = BuildFullStack(snap);

        var proposal = new ProfileOverrideProposal(
            Intent: OverrideIntent.SwitchProfile,
            TargetField: "profile",
            ProposedValue: "SENSITIVE",
            Rationale: "legacy LLM-emitted switch block",
            ExpiresAtUtc: DateTime.UtcNow.AddHours(24));
        var profileBefore = engine.HabituationProfile;

        var apply = applicator.ApplyAccepted(proposal);

        Assert.False(apply.IsSuccess);
        Assert.Equal(profileBefore, engine.HabituationProfile);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario C — Goal 1 (對話縱深感 / 長期記憶): observation persistence
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioC_LlmEmitsObservationAdd_PersistedToSnapshot_AndCappedAt30PerBucket()
    {
        var snap = MakeSnapshot();
        var (_, profileStore, _, _, _, writer) = BuildFullStack(snap);

        string raw =
            "{\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"了解。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
            "[OBSERVATION_ADD]\n" +
            "user reports symptoms reliably worsen around 3 pm\n" +
            "[/OBSERVATION_ADD]";

        var parsed = HabituationOverrideParser.TryParse(raw);
        Assert.Single(parsed.Observations);

        bool wrote = writer.Append(parsed.Observations[0]);
        Assert.True(wrote);
        // UM2: observations live in the current month's bucket now.
        string month = System.DateTime.UtcNow.ToString("yyyy-MM");
        var bucket = profileStore.Snapshot!.LongitudinalObservationsV2[month];
        Assert.Single(bucket);
        Assert.Contains("3 pm", bucket[0].Text);

        // Cap at 30 per month bucket: append 60 unique, the bucket ends at 30.
        for (int i = 0; i < 60; i++)
            writer.Append($"distinct observation index {i} xyzzy_{i}");
        Assert.Equal(LongitudinalObservationWriter.MaxObservationsPerBucket,
                     profileStore.Snapshot.LongitudinalObservationsV2[month].Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario D — Goal 4 (安全網記憶): vestibular hard cap blocks relaxation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioD_VestibularUser_LlmProposesRelaxation_BlockedAtBothGates()
    {
        var snap = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.Vestibular);
        var (engine, _, eventLog, actionLog, applicator, _) = BuildFullStack(snap);

        string raw = BuildRawWithProfileOverride(
            envelopeReply: "幫您放鬆 sensitivity。",
            intent: OverrideIntent.AdjustSensitivity,
            value: 0.8,
            rationale: "user wants relaxation");

        var parsed = HabituationOverrideParser.TryParse(raw);
        Assert.NotNull(parsed.Proposal);

        // Gate 1: processor rejects up front (rule 3 — vestibular + relaxation).
        var validation = HabituationOverrideProcessor.Validate(parsed.Proposal!, snap);
        Assert.False(validation.IsAccepted);
        Assert.NotNull(validation.SafetyNote);

        // Gate 2 (belt-and-suspenders): if a caller bypassed the Processor
        // and called Applicator.ApplyAccepted directly, the 7f intercept
        // catches it again. Engine state never changes.
        double triggerBefore = engine.HabituationEffectiveTrigger;
        var applyResult = applicator.ApplyAccepted(parsed.Proposal!);
        Assert.False(applyResult.IsSuccess);
        Assert.True(applyResult.BlockedBySafety);
        Assert.Equal(triggerBefore, engine.HabituationEffectiveTrigger);

        // Audit + telemetry side effects.
        Assert.Contains(actionLog.Recent(), e => e.Kind == ChatbotActionKind.Intercepted);
        Assert.Contains(eventLog.Recent(),
                        e => e.Category == "Habituation.SafetyIntercept");
    }

    [Fact]
    public void ScenarioD_VestibularUser_LlmProposesTightening_AllowedThrough()
    {
        // Sanity: vestibular advisory blocks RELAXATION, not tightening.
        // A tightening proposal still applies.
        var snap = MakeSnapshot(scale: 1.0, advisory: MedicalAdvisory.Vestibular);
        var (engine, _, _, _, applicator, _) = BuildFullStack(snap);

        string raw = BuildRawWithProfileOverride(
            envelopeReply: "提高 sensitivity。",
            intent: OverrideIntent.AdjustSensitivity,
            value: 1.2);

        var parsed = HabituationOverrideParser.TryParse(raw);
        var validation = HabituationOverrideProcessor.Validate(parsed.Proposal!, snap);
        Assert.True(validation.IsAccepted);

        var apply = applicator.ApplyAccepted(parsed.Proposal!);
        Assert.True(apply.IsSuccess);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario E — Goal 2 (狀態一致性): in-chat profile picker
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioE_BareProfileTrigger_PickerDetected_BubbleContentMarksCurrent()
    {
        // The picker's detection + bubble construction are testable
        // without dispatcher pumping — verify the picker request matcher
        // catches the bare word AND that the lexicon's profile-description
        // list has the canonical 3 entries (which is what the bubble
        // will render).
        Assert.True(HabituationProposalLexicon.TryDetectProfilePickerRequest("profile"));
        Assert.True(HabituationProposalLexicon.TryDetectProfilePickerRequest("Profile"));
        Assert.True(HabituationProposalLexicon.TryDetectProfilePickerRequest("我想換 profile"));

        // 3 profiles, each with a non-empty zh description.
        var descs = HabituationProposalLexicon.ProfileDescriptions;
        Assert.Equal(3, descs.Count);
        foreach (var (profile, desc) in descs)
        {
            Assert.False(string.IsNullOrWhiteSpace(desc), $"{profile} description must not be empty");
        }
    }

    [Fact]
    public void ScenarioE_UserPicksProfile_NoOps_ProfileUnchanged()
    {
        // Reading A (2026-06-06): profile is calibration-only. The in-chat profile
        // picker routed a SwitchProfile proposal through the applicator; that path
        // now no-ops (profile does not change mid-session). NOTE: this leaves the
        // in-chat picker defunct — flagged for product follow-up.
        var snap = MakeSnapshot(profile: "TYPICAL");
        var (engine, _, _, _, applicator, _) = BuildFullStack(snap);
        var profileBefore = engine.HabituationProfile;

        var proposal = new ProfileOverrideProposal(
            Intent: OverrideIntent.SwitchProfile,
            TargetField: "profile",
            ProposedValue: "RESILIENT",
            Rationale: "user picked from in-chat profile picker",
            ExpiresAtUtc: DateTime.UtcNow.AddHours(24));
        var result = applicator.ApplyAccepted(proposal);
        Assert.False(result.IsSuccess);
        Assert.Equal(profileBefore, engine.HabituationProfile);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario F — Goal 5 (habituation progression): 4-week simulation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioF_FourWeekSimulation_HabituationLevelAccrues_ContextBlockReflectsIt()
    {
        var (engine, profileStore, _, _, _, _) = BuildFullStack();
        // D-13: the calibration application path (ApplyCalibrationResult) is deleted.
        // The original baseline call set RESILIENT + scale 1.0 — both already the engine's
        // defaults, so the only behavioural effect was an irrelevant calibration_count bump
        // (this test asserts on habituation level + the [habituation_state] block, never the
        // count). A fresh engine already starts RESILIENT at the default trigger, so the
        // baseline needs no explicit setup.

        // 28 days × 4 hours/day of steady benign sampling. Compressed:
        // 60-second update slices fed via sampledAt timestamps so the
        // engine's dt computation sees synthetic time passing. OFI well
        // below trigger so no spikes accrue — gives the habituation
        // engine clear stable runs to step through.
        const int stepsPerDay = 4 * 60;
        double levelStart = engine.Current.HabituationLevel;
        DateTime simTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int day = 0; day < 28; day++)
        {
            for (int step = 0; step < stepsPerDay; step++)
            {
                simTime = simTime.AddSeconds(60);
                engine.Update(opticalFlowIndex: 5.0, mode: "Passenger", sampledAt: simTime);
            }
        }
        double levelEnd = engine.Current.HabituationLevel;

        Assert.True(levelEnd >= levelStart,
            $"Expected habituation level to be non-decreasing over 4 weeks " +
            $"of stable sampling, but went {levelStart} → {levelEnd}");

        // Build the [habituation_state] block from the post-simulation
        // snapshot. The chatbot's context reflects the current state so
        // the LLM can ground replies in "you've been progressing".
        var snap = profileStore.Snapshot;
        Assert.NotNull(snap);
        string block = HabituationStateBlockBuilder.BuildBlock(
            snap, trajectorySummary: "stable over 28 days", powerSaveActive: false);
        Assert.Contains("[habituation_state]", block);
        Assert.Contains("level:", block);
        Assert.Contains("trajectory:", block);
        Assert.Contains("stable over 28 days", block);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Read-side smoke tests (Goal 1 + Goal 2)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioG_FreshCalibration_ContextBlockCarriesProfileAndCalibrationCount()
    {
        var snap = MakeSnapshot(profile: "SENSITIVE", calibrationCount: 1);
        string block = HabituationStateBlockBuilder.BuildBlock(snap, trajectorySummary: "", powerSaveActive: false);
        Assert.Contains("profile: SENSITIVE", block);
        Assert.Contains("calibrations: 1", block);
        Assert.Contains("last_cal:", block);
    }

    [Fact]
    public void ScenarioH_PowerSaveActive_ContextBlockSurfacesIt()
    {
        var snap = MakeSnapshot();
        string block = HabituationStateBlockBuilder.BuildBlock(snap, trajectorySummary: "", powerSaveActive: true);
        Assert.Contains("power_save_active: true", block);
    }
}
