using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Stage 7d — End-to-end acceptance smoke for the Calibration feature.
//
// Six scenarios, all required to run and pass. These tests deliberately
// exercise the FULL production pipeline boundary-to-boundary (Processor →
// Applicator → MockInferenceEngine → FileHabituationProfileStore JSON write)
// rather than mocking out the in-between layers. They are slower than the
// per-stage unit tests but they're the proof that Calibration actually does
// what the user-facing spec promises.
//
// Scenarios:
//   1. First-time flow      — buttons UI → engine → profile JSON v2 round-trip
//   2. Subsequent flow      — LLM stub returns parsed delta → Completed → JSON
//   3. Safety floor         — self-harm keyword → SafetyFloorTriggered, no JSON
//   4. Q3 fallback          — confidence below 0.7 → RequestQ3Fallback → merge
//   5. Vestibular path      — VestibularDiagnosed → override 50, strike 1
//   6. App-data-wipe path   — habituation.json only → WasRecoveredFromWipe=true
//
// Scenario 6 depends on Stage 7b's production wiring (MainViewModel passes
// HabituationStatePath as the wipe-probe path). The test constructs a real
// FileHabituationProfileStore the same way MainViewModel does so a regression
// that drops the second ctor arg shows up here as a failure.
// ─────────────────────────────────────────────────────────────────────────────

public class CalibrationEndToEndSmoke : IDisposable
{
    private readonly string _tempDir;
    private readonly string _profilePath;
    private readonly string _wipeProbePath;
    private readonly string _transcriptPath;

    public CalibrationEndToEndSmoke()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _profilePath    = Path.Combine(_tempDir, "habituation_profile.json");
        _wipeProbePath  = Path.Combine(_tempDir, "habituation.json");
        _transcriptPath = Path.Combine(_tempDir, "calibration_history.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Scenario 1: first-time flow ──────────────────────────────────────────
    [Fact]
    public void Scenario1_FirstTimeFlow_AppliesProfileAndWritesV2Snapshot()
    {
        // Simulate the demo finishing with no prior profile JSON. MainViewModel's
        // LoadedCalibrationCount path would route this through the 5-card UI.
        // Here we exercise the CalibrationApplicator (the shared code path the
        // card's Completed event calls) and then verify the persisted snapshot.
        var store = new FileHabituationProfileStore(_profilePath, _wipeProbePath);
        var engine = new MockInferenceEngine(profileStore: store);

        // First-time signal: nothing persisted yet, so the engine reads 0.
        Assert.Equal(0, engine.LoadedCalibrationCount);

        var messages = new ObservableCollection<ChatMessageViewModel>();
        var responses = new CalibrationResponses
        {
            Q1Segment    = UncomfortableSegment.Middle,
            Q1Freetext   = null,
            Q2Symptoms   = new[] { SymptomType.Dizziness, SymptomType.Nausea },
            Q3Intensity  = 3,
            Q4Comparison = BaselineComparison.Similar,
            Q5RiskFactors = new[] { RiskFactor.None },
        };
        var sensor = new CalibrationSensorData(PeakPressure: 65.0, MaskActivations: 1, PeakPressureAtSec: 30.0);

        var result = CalibrationApplicator.Apply(responses, sensor, engine, messages);

        // Engine surface: profile applied, source tagged.
        Assert.Equal(CalibrationCompleteness.Complete, result.Completeness);
        Assert.Equal(HabituationProfile.Typical, engine.HabituationProfile);
        Assert.Equal("calibration_demo", engine.HabituationProfileSource);

        // Summary message posted (single bubble — no advisory for this combo).
        Assert.Single(messages);
        Assert.Contains("Calibration 完成", messages[0].Content);

        // Persisted snapshot exists and is v2-shaped.
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.SchemaVersion);
        Assert.Equal("TYPICAL", loaded.Profile);
        Assert.Equal("calibration_demo", loaded.ProfileSource);
        // Stage 7f: a Complete result MUST bump calibration_count from 0 to 1
        // AND persist last_calibration_at_iso. Without this, Stage 7a's
        // branch reads forever-zero and the LLM banner is unreachable in
        // production. The engine surface MUST match the persisted value.
        Assert.Equal(1, loaded.CalibrationCount);
        Assert.Equal(1, engine.LoadedCalibrationCount);
        Assert.False(string.IsNullOrEmpty(loaded.LastCalibrationAtIso));
        // The wipe-recovery flag MUST be false on a real save — it's a one-shot
        // loader-only flag and the writer scrubs it.
        Assert.False(loaded.WasRecoveredFromWipe);
    }

    // ── Scenario 2: subsequent flow via LLM stub ─────────────────────────────
    [Fact]
    public async Task Scenario2_SubsequentFlow_LlmDeltaCompletesAndWritesProfile()
    {
        // Seed the profile store with a prior snapshot so calibration_count=1
        // is the persisted starting point.
        var store = new FileHabituationProfileStore(_profilePath, _wipeProbePath);
        var priorSnapshot = new HabituationProfileSnapshot
        {
            UpdatedAtIso   = DateTime.UtcNow.ToString("o"),
            Profile        = "TYPICAL",
            ProfileSource  = "calibration_demo",
            CalibrationCount = 1,
            LastCalibrationAtIso = DateTime.UtcNow.AddDays(-30).ToString("o"),
        };
        store.Save(priorSnapshot);

        // New engine sees calibration_count=1 — would route through banner in MainViewModel.
        var engine = new MockInferenceEngine(profileStore: store);
        Assert.Equal(1, engine.LoadedCalibrationCount);

        // LLM stub returns a complete delta with high confidence.
        var llm = new StubLlmClient();
        llm.EnqueueReply(BuildDeltaReply(
            q1: "Middle", q2: new[] { "Dizziness" }, q3: 2, q4: "Better", q5: new[] { "None" },
            isComplete: true, confidence: new[] { 0.9, 0.8, 0.85, 0.7, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, _transcriptPath);

        CalibrationResponses? completedResponses = null;
        bool completedFired = false;
        svc.Completed += (resp, _) => { completedResponses = resp; completedFired = true; };

        await svc.StartAsync(priorSnapshot, new CalibrationSensorData(50.0, 1, 20.0));
        await svc.SendUserMessageAsync("這次中段最不舒服");

        Assert.True(completedFired);
        Assert.NotNull(completedResponses);
        Assert.Equal(2, completedResponses!.Q3Intensity);

        // Now apply the LLM-derived responses through the same applicator path.
        var messages = new ObservableCollection<ChatMessageViewModel>();
        var sensor = new CalibrationSensorData(50.0, 1, 20.0);
        var result = CalibrationApplicator.Apply(completedResponses, sensor, engine, messages);

        Assert.Equal(CalibrationCompleteness.Complete, result.Completeness);
        // Profile was applied; snapshot was rewritten with the new profile.
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(engine.HabituationProfile.ToString().ToUpperInvariant(), loaded!.Profile);
        // Stage 7f: subsequent calibration bumps from the prior count of 1 to 2.
        Assert.Equal(2, loaded.CalibrationCount);
        Assert.Equal(2, engine.LoadedCalibrationCount);
        Assert.Single(messages);
    }

    // ── Scenario 3: self-harm safety floor short-circuits LLM ────────────────
    [Fact]
    public async Task Scenario3_SafetyFloor_TriggeredBeforeLlmCall_NoProfileChange()
    {
        var store = new FileHabituationProfileStore(_profilePath, _wipeProbePath);
        var engine = new MockInferenceEngine(profileStore: store);
        var profileBefore = engine.HabituationProfile;
        double triggerBefore = engine.HabituationEffectiveTrigger;

        var llm = new StubLlmClient(); // Replies queue stays empty — must NOT be hit.
        using var svc = new CalibrationChatbotService(llm, _transcriptPath);

        string? safetyResponseSeen = null;
        bool completedFired = false;
        svc.SafetyFloorTriggered += msg => safetyResponseSeen = msg;
        svc.Completed += (_, _) => completedFired = true;

        await svc.StartAsync(null, new CalibrationSensorData(0, 0, 0));
        await svc.SendUserMessageAsync("我不想活了");

        // SafetyFloorTriggered fired before any LLM call, session ended, Completed did NOT fire.
        Assert.NotNull(safetyResponseSeen);
        Assert.Contains("1925", safetyResponseSeen!); // zh crisis line marker
        Assert.Equal(0, llm.CallCount);
        Assert.False(svc.IsActive);
        Assert.False(completedFired);

        // No profile mutation, no snapshot file written.
        Assert.Equal(profileBefore, engine.HabituationProfile);
        Assert.Equal(triggerBefore, engine.HabituationEffectiveTrigger, precision: 2);
        Assert.Null(engine.LastCalibrationResult);
    }

    // ── Scenario 4: Q3 confidence fallback → MergeFallbackAndComplete ────────
    [Fact]
    public async Task Scenario4_Q3Fallback_RequestedAndMergedCompletesOnce()
    {
        var llm = new StubLlmClient();
        // LLM says is_complete=true but Q3 confidence 0.5 (< 0.7 threshold).
        llm.EnqueueReply(BuildDeltaReply(
            q1: null, q2: null, q3: 3, q4: null, q5: null,
            isComplete: true, confidence: new[] { 0.0, 0.0, 0.5, 0.0, 0.0 }));
        using var svc = new CalibrationChatbotService(llm, _transcriptPath);

        bool fallbackRequested = false;
        var completedEvents = new List<(CalibrationResponses Resp, bool IsComplete)>();
        svc.RequestQ3Fallback += () => fallbackRequested = true;
        svc.Completed += (resp, complete) => completedEvents.Add((resp, complete));

        await svc.StartAsync(null, new CalibrationSensorData(50, 1, 20));
        await svc.SendUserMessageAsync("not sure, maybe a 3");

        Assert.True(fallbackRequested);
        Assert.Empty(completedEvents); // deferred — fallback path holds Completed
        Assert.True(svc.IsActive);

        // Caller routes the Q3 fallback button answer back through merge.
        svc.MergeFallbackAndComplete(q3Intensity: 4, q3Freetext: "actually felt like a 4");

        Assert.Single(completedEvents);
        Assert.Equal(4, completedEvents[0].Resp.Q3Intensity);
        Assert.Equal(CalibrationSource.FallbackButtons, completedEvents[0].Resp.Q3Source);
        // is_complete reflects what the LLM reported pre-fallback.
        Assert.True(completedEvents[0].IsComplete);
        Assert.False(svc.IsActive);
    }

    // ── Scenario 5: vestibular hard-lock path ────────────────────────────────
    [Fact]
    public void Scenario5_VestibularPath_OverrideAndStrikeLimitApplied()
    {
        var store = new FileHabituationProfileStore(_profilePath, _wipeProbePath);
        var engine = new MockInferenceEngine(profileStore: store);
        var messages = new ObservableCollection<ChatMessageViewModel>();

        var responses = new CalibrationResponses
        {
            Q1Segment    = UncomfortableSegment.Middle,
            Q2Symptoms   = new[] { SymptomType.Dizziness, SymptomType.Imbalance },
            Q3Intensity  = 4,
            Q4Comparison = BaselineComparison.Worse,
            Q5RiskFactors = new[] { RiskFactor.VestibularDiagnosed },
        };
        var sensor = new CalibrationSensorData(60, 2, 25);

        var result = CalibrationApplicator.Apply(responses, sensor, engine, messages);

        // Processor: SENSITIVE + override 50 + strike limit 1 + vestibular advisory.
        Assert.Equal(HabituationProfile.Sensitive, result.Profile);
        Assert.Equal(50, result.TriggerThresholdOverride);
        Assert.Equal(1, result.SevereStrikeLimitOverride);
        Assert.Equal("vestibular", result.MedicalAdvisory);

        // Engine: overrides applied (effective trigger = 50, not 85).
        Assert.Equal(HabituationProfile.Sensitive, engine.HabituationProfile);
        Assert.Equal(50.0, engine.HabituationEffectiveTrigger, precision: 2);
        Assert.Equal(1, engine.HabituationSevereStrikeLimitOverride);

        // Two messages: summary + medical advisory (posted exactly once).
        Assert.Equal(2, messages.Count);
        Assert.Contains("Calibration 完成", messages[0].Content);
        Assert.Contains("vestibular condition", messages[1].Content);
    }

    // ── Scenario 6: app-data-wipe recovery ───────────────────────────────────
    [Fact]
    public void Scenario6_AppDataWipe_LoaderReturnsWipeRecoverySnapshot()
    {
        // Wipe scenario: habituation.json exists (legacy offset file from a
        // prior install), habituation_profile.json does NOT. Construct the
        // store the SAME way MainViewModel does (Stage 7b production wiring).
        File.WriteAllText(_wipeProbePath, "{\"offset\":2.0}");
        Assert.False(File.Exists(_profilePath));

        // Two-arg ctor mirrors the Stage 7b MainViewModel field initialiser.
        // If a future refactor drops the second arg, this assertion fails
        // because the loader returns null on a missing profile file when no
        // probe path is supplied.
        var store = new FileHabituationProfileStore(_profilePath, _wipeProbePath);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.True(loaded!.WasRecoveredFromWipe,
            "Wipe-detection failed — production wiring (Stage 7b) likely regressed.");
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(0, loaded.CalibrationCount);
        // The "treat as first-run for calibration" pipeline relies on
        // CalibrationCount==0 here. MainViewModel.OnDemoVideoFinished
        // (Stage 7a) consults MockInferenceEngine.LoadedCalibrationCount,
        // which sources from this exact field. A wipe-recovered user
        // therefore re-routes through the first-time card path — verified
        // by constructing the engine with the same store and inspecting
        // its loaded count.
        var engine = new MockInferenceEngine(profileStore: store);
        Assert.Equal(0, engine.LoadedCalibrationCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Stub ICalibrationLlmClient: returns canned replies from a
    /// queue. Mirrors the StubLlm pattern in CalibrationChatbotServiceTests but
    /// is self-contained so this file doesn't depend on test-internal types.</summary>
    private sealed class StubLlmClient : ICalibrationLlmClient
    {
        private readonly Queue<string?> _replies = new();
        public int CallCount { get; private set; }

        public void EnqueueReply(string reply) => _replies.Enqueue(reply);

        public Task<string?> SendChatAsync(string systemPrompt,
                                            IReadOnlyList<ChatMessage> history,
                                            CancellationToken ct = default)
        {
            CallCount++;
            string? reply = _replies.Count > 0 ? _replies.Dequeue() : null;
            return Task.FromResult(reply);
        }
    }

    /// <summary>Build a [CALIBRATION_DELTA]-wrapped JSON reply for a stub
    /// LLM. Matches the schema documented in CalibrationLlmDeltaParser. Any
    /// argument left null is omitted from the delta block.</summary>
    private static string BuildDeltaReply(
        string? q1, string[]? q2, int? q3, string? q4, string[]? q5,
        bool isComplete, double[] confidence)
    {
        string Field(string key, string? value) =>
            value is null ? $"\"{key}\": null" : $"\"{key}\": \"{value}\"";
        string ArrField(string key, string[]? value) =>
            value is null
                ? $"\"{key}\": null"
                : $"\"{key}\": [{string.Join(",", value.Select(v => $"\"{v}\""))}]";
        string IntField(string key, int? value) =>
            value is null ? $"\"{key}\": null" : $"\"{key}\": {value.Value}";

        string delta = $"{{{Field("q1_segment", q1)}, {ArrField("q2_symptoms", q2)}, " +
                       $"{IntField("q3_intensity", q3)}, {Field("q4_comparison", q4)}, " +
                       $"{ArrField("q5_risk_factors", q5)}}}";
        string conf = $"{{\"q1\": {confidence[0]}, \"q2\": {confidence[1]}, " +
                      $"\"q3\": {confidence[2]}, \"q4\": {confidence[3]}, \"q5\": {confidence[4]}}}";

        return $@"OK noted.
[CALIBRATION_DELTA]
{{
  ""delta"": {delta},
  ""confidence"": {conf},
  ""is_complete"": {(isComplete ? "true" : "false")}
}}
[/CALIBRATION_DELTA]";
    }
}
