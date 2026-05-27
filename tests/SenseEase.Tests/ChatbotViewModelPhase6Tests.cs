using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M2 Phase 6e: scenario tests verifying that <c>ChatbotViewModel</c>
/// embeds the <c>[habituation_state]</c> block into the contextual
/// input passed to the LLM. Each scenario maps to one of the user
/// goals from advisor Section 1:
///   Goal 1 (對話縱深感)   — block carries calibration history
///   Goal 2 (狀態一致性)    — Power Save state surfaces correctly
///   Goal 5 (habituation progression) — trajectory line present
///
/// <para>These tests stub the LLM client entirely — they verify the
/// INPUT we hand the LLM, not the LLM's output. The actual reply
/// grounding (does the LLM follow the rules in the system prompt?)
/// is a Phase 8 acceptance concern requiring a live model.</para>
/// </summary>
public class ChatbotViewModelPhase6Tests
{
    // ── Stubs ──────────────────────────────────────────────────────────────

    private sealed class StubChatbotService : IChatbotService
    {
        public bool IsModelAvailable => false;
        public bool IsModelLoaded    => false;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult(new NluResult { Intent = "Unknown", Response = string.Empty });
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => System.Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(
        HabituationProfileSnapshot? snapshot,
        bool powerSaveActive,
        InMemoryEventLogStore? eventLog = null)
    {
        var engine = new MockInferenceEngine();
        var vm = new ChatbotViewModel(
            chatbot: new StubChatbotService(),
            engine: engine,
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false,
            eventLog: eventLog);
        vm.LoadHabituationSnapshot = () => snapshot;
        vm.IsPowerSaveActive       = () => powerSaveActive;
        return vm;
    }

    // ── Scenario A — Goal 1 (對話縱深感) ───────────────────────────────────

    [Fact]
    public void Scenario_A_FreshCalibration_BlockReferencesCalibrationHistory()
    {
        // User just completed calibration. Block should carry the
        // calibration_count + last_cal date so the LLM can ground its
        // first reply in "your recent calibration..."
        var snap = new HabituationProfileSnapshot
        {
            Profile             = "SENSITIVE",
            CurrentVimsTier     = 3,
            HabituationLevel    = 10.0,
            EngineState         = "RUNNING",
            CalibrationCount    = 1,
            LastCalibrationAtIso = "2026-05-27T08:00:00.0000000Z",
        };
        var vm = BuildVm(snap, powerSaveActive: false);

        string ctx = vm.BuildContextualInput("hi");

        Assert.Contains("[habituation_state]", ctx);
        Assert.Contains("calibrations: 1", ctx);
        Assert.Contains("last_cal: 2026-05-27", ctx);
        Assert.Contains("profile: SENSITIVE", ctx);
    }

    // ── Scenario E — Goal 2 (狀態一致性) ───────────────────────────────────

    [Fact]
    public void Scenario_E_PowerSaveActive_BlockContainsPowerSaveFlag()
    {
        var snap = new HabituationProfileSnapshot
        {
            Profile          = "RESILIENT",
            CurrentVimsTier  = 4,
            HabituationLevel = 8.5,
            EngineState      = "RUNNING",
        };
        var vm = BuildVm(snap, powerSaveActive: true);

        string ctx = vm.BuildContextualInput("how am I doing");

        Assert.Contains("power_save_active: true", ctx);
    }

    [Fact]
    public void Scenario_E_PowerSaveInactive_BlockOmitsFlag()
    {
        var snap = new HabituationProfileSnapshot { Profile = "RESILIENT", EngineState = "RUNNING" };
        var vm = BuildVm(snap, powerSaveActive: false);

        string ctx = vm.BuildContextualInput("hi");

        Assert.DoesNotContain("power_save_active", ctx);
    }

    // ── Scenario — Goal 5 (habituation progression) ───────────────────────

    [Fact]
    public void Scenario_LongitudinalProgression_BlockContainsTrajectoryLine()
    {
        var snap = new HabituationProfileSnapshot
        {
            Profile          = "TYPICAL",
            CurrentVimsTier  = 4,
            HabituationLevel = 14.0,
            EngineState      = "RUNNING",
        };
        var eventLog = new InMemoryEventLogStore();
        var now = DateTime.UtcNow;
        // Seed habituation events the trajectory builder will pick up
        eventLog.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-7),  Message = "level rose 5 → 10" });
        eventLog.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-3),  Message = "level rose 10 → 14" });
        eventLog.Append(new EventLogEntry { Category = "Habituation.Spike",        Timestamp = now.AddDays(-5),  Message = "spike" });

        var vm = BuildVm(snap, powerSaveActive: false, eventLog: eventLog);
        string ctx = vm.BuildContextualInput("how have I progressed");

        Assert.Contains("trajectory:", ctx);
        Assert.Contains("2 level changes", ctx);
        Assert.Contains("1 spike", ctx);
    }

    [Fact]
    public void Scenario_LongitudinalObservations_BlockListsRecentMostFirst()
    {
        var snap = new HabituationProfileSnapshot
        {
            Profile          = "TYPICAL",
            EngineState      = "RUNNING",
            LongitudinalObservations = new List<string>
            {
                "2026-05-01: oldest observation",
                "2026-05-15: middle observation",
                "2026-05-25: newest observation",
            },
        };
        var vm = BuildVm(snap, powerSaveActive: false);

        string ctx = vm.BuildContextualInput("anything new");

        Assert.Contains("recent_observations:", ctx);
        int newest = ctx.IndexOf("newest observation");
        int oldest = ctx.IndexOf("oldest observation");
        Assert.True(newest > 0 && oldest > 0);
        Assert.True(newest < oldest, "newest should appear before oldest in block");
    }

    // ── Pre-calibration (no snapshot) ──────────────────────────────────────

    [Fact]
    public void NoSnapshot_BlockOmittedEntirely()
    {
        // User hasn't calibrated yet. Block should be absent — chatbot
        // continues to receive [RecentAdjustments] + [SenseEaseState]
        // exactly as before Phase 6c.
        var vm = BuildVm(snapshot: null, powerSaveActive: false);

        string ctx = vm.BuildContextualInput("hi");

        Assert.DoesNotContain("[habituation_state]", ctx);
        Assert.DoesNotContain("[/habituation_state]", ctx);
        // Existing pre-Phase-6 behaviour preserved
        Assert.Contains("[SenseEaseState", ctx);
        Assert.Contains("User: hi", ctx);
    }

    // ── Order — block comes BEFORE [SenseEaseState] ────────────────────────

    [Fact]
    public void BlockOrder_HabituationStatePrecededesSenseEaseState()
    {
        var snap = new HabituationProfileSnapshot { Profile = "TYPICAL", EngineState = "RUNNING" };
        var vm = BuildVm(snap, powerSaveActive: false);

        string ctx = vm.BuildContextualInput("test");

        int habIdx = ctx.IndexOf("[habituation_state]");
        int seIdx  = ctx.IndexOf("[SenseEaseState");
        Assert.True(habIdx >= 0);
        Assert.True(seIdx > habIdx, "[habituation_state] must precede [SenseEaseState]");
    }
}
