using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 Layer-2 (W5) chatbot hardening at the ChatbotViewModel.SendAsync layer:
/// <list type="bullet">
///   <item>H5 — OBSERVATION_ADD persistence is capped at 2 PER SESSION (the parser
///   already caps at 2 per response; this bounds the TOTAL so a looping / injected
///   model can't slowly fill the profile's longitudinal memory), reset by Clear().</item>
///   <item>NE6 — an interactive LLM call that cancels / times out surfaces a calm
///   retry hint, never a raw "[Error] canceled", and clears the typing indicator.</item>
/// </list>
///
/// <para>The stub LLM returns an already-completed task and reports IsModelLoaded=true
/// (which disables the async fake-stream), so SendAsync runs to completion synchronously
/// on the test thread — the same harness shape as ChatbotViewModelSafetyFloorTests.</para>
/// </summary>
public class ChatbotViewModelInjectionHardeningTests
{
    // Mirror the safety-floor test's sandbox: steer SpecialFolder.LocalApplicationData
    // at a temp dir BEFORE AppConfig's static path is first read, so constructing a VM
    // never appends to the developer's real chat_history.jsonl. Best-effort.
    static ChatbotViewModelInjectionHardeningTests()
    {
        try
        {
            string sandbox = Path.Combine(
                Path.GetTempPath(), "SenseEase_Tests_LocalAppData_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", sandbox);
        }
        catch { /* best-effort isolation */ }
    }

    /// <summary>LLM stub whose reply is supplied by a delegate. IsModelLoaded=true so
    /// the VM takes the synchronous (non-fake-stream) render path.</summary>
    private sealed class StubChatbotService : IChatbotService
    {
        private readonly Func<Task<NluResult>> _send;
        public StubChatbotService(Func<Task<NluResult>> send) => _send = send;
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => true;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default) => _send();
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(Func<Task<NluResult>> responder)
        => new ChatbotViewModel(
            chatbot: new StubChatbotService(responder),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);

    // A reply carrying TWO distinct OBSERVATION_ADD blocks (the parser's per-response
    // max). The envelope prose "noted" is what the visible bubble shows.
    private static NluResult TwoObservations() => new NluResult
    {
        Intent = "Unknown",
        Response = "noted",
        RawModelOutput =
            "noted\n" +
            "[OBSERVATION_ADD]user mentions mornings feel unique alpha[/OBSERVATION_ADD]\n" +
            "[OBSERVATION_ADD]user mentions evenings feel unique bravo[/OBSERVATION_ADD]",
    };

    [Fact]
    public void H5_ObservationWrites_CappedAtTwoPerSession_AndResetByClear()
    {
        int writes = 0;
        var vm = BuildVm(() => Task.FromResult(TwoObservations()));
        vm.LongitudinalObservationCallback = _ => writes++;

        // Three turns, each carrying 2 observations → 6 dispatched if uncapped.
        for (int i = 0; i < 3; i++)
        {
            vm.InputText = $"turn {i}: I felt a little off today";
            vm.SendCommand.Execute(null);
        }

        // H5: total observation writes are capped at 2 PER SESSION, not 6.
        Assert.Equal(2, writes);

        // A new session (Clear) restores the budget.
        vm.ClearCommand.Execute(null);
        writes = 0;
        vm.InputText = "after clear: I felt off again";
        vm.SendCommand.Execute(null);
        Assert.Equal(2, writes);
    }

    /// <summary>Records whether ClearOnDiskTrace was invoked — pins the H24 wiring
    /// that "clear chat" also wipes the on-disk model trace.</summary>
    private sealed class TraceSpyChatbotService : IChatbotService
    {
        public bool TraceCleared { get; private set; }
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => true;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" });
        public void ClearHistory() { }
        public void ClearOnDiskTrace() => TraceCleared = true;
        public void Dispose() { }
    }

    [Fact]
    public void H24_ClearChat_WipesOnDiskTrace()
    {
        var spy = new TraceSpyChatbotService();
        var vm = new ChatbotViewModel(
            chatbot: spy,
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);

        Assert.False(spy.TraceCleared);
        vm.ClearCommand.Execute(null);
        Assert.True(spy.TraceCleared); // H24: clear-chat wipes the on-disk trace too
    }

    [Fact]
    public void UM5_BehaviorTrendBlock_InjectedIntoContextualInput_WhenEnoughHistory()
    {
        var vm = new ChatbotViewModel(
            chatbot: new TraceSpyChatbotService(),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);

        var rows = new List<DailyBehaviorAggregate>();
        for (int i = 0; i < 10; i++)
            rows.Add(new DailyBehaviorAggregate { Date = $"2026-06-{i + 1:00}", OfiP90 = 50, TierModal = 4 });
        vm.LoadBehaviorAggregates = () => rows;

        string ctx = vm.BuildContextualInput("how am I doing lately?");
        Assert.Contains("[behavior_trend]", ctx); // UM5 block injected via the host hook

        // With < 7 days the block is omitted.
        vm.LoadBehaviorAggregates = () => new List<DailyBehaviorAggregate> { rows[0] };
        Assert.DoesNotContain("[behavior_trend]", vm.BuildContextualInput("again?"));
    }

    [Fact]
    public void NE6_CancelledLlmCall_SurfacesCalmRetry_NotRawError()
    {
        // The interactive call cancels / times out (TaskCanceledException from
        // HttpClient.Timeout also derives from OperationCanceledException).
        var vm = BuildVm(() => Task.FromException<NluResult>(new OperationCanceledException()));

        vm.InputText = "how should I set things up?";
        vm.SendCommand.Execute(null);

        // NE6: the cancelled turn must surface the calm bilingual retry hint, never a
        // raw "[Error] ...", and must clear the typing indicator. Asserted over the
        // whole transcript (not Single) because the VM loads any prior on-disk history
        // at construction — robust to that pre-existing state.
        Assert.DoesNotContain(vm.Messages, m => m.Content.Contains("[Error]"));
        Assert.Contains(vm.Messages, m => m.Role == ChatRole.Assistant
            && (m.Content.Contains("請再試一次") || m.Content.Contains("please try again")));
        Assert.DoesNotContain(vm.Messages, m => m.IsTyping);
        Assert.False(vm.IsGenerating);
    }
}
