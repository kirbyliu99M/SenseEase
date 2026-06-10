using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Phase 7 (RESUME_ACKNOWLEDGE) wiring: when the LLM reply carries a
/// [RESUME_ACKNOWLEDGE] block with user_confirmed:true, ChatbotViewModel.SendAsync must invoke
/// AcknowledgeResumeCallback (the host wires it to MockInferenceEngine.AcknowledgeAndResumeHabituation,
/// a guarded no-op unless actually awaiting confirm). user_confirmed:false must NOT resume.
/// Mirrors the StubChatbotService harness from ChatbotViewModelInjectionHardeningTests.</summary>
[Collection("TestDataRoot")]
public class ChatbotResumeAckWiringTests
{
    // CW-9: route the VM's chat_history.jsonl writes into a per-class temp subdir
    // via the real data-root seam (the old LOCALAPPDATA env-var trick was a no-op —
    // GetFolderPath ignores it). The [Collection] serialises this against the other
    // override-retargeting classes so the single static override never races.
    public ChatbotResumeAckWiringTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(ChatbotResumeAckWiringTests));
    }

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

    private static NluResult ReplyWithResumeAck(bool confirmed, string quote) => new NluResult
    {
        Intent = "Unknown",
        Response = "okay",
        RawModelOutput =
            "okay\n" +
            $"[RESUME_ACKNOWLEDGE]{{\"user_confirmed\": {(confirmed ? "true" : "false")}, \"user_quote\": \"{quote}\"}}[/RESUME_ACKNOWLEDGE]",
    };

    [Fact]
    public void ResumeAck_UserConfirmedTrue_InvokesCallbackWithQuote()
    {
        string? capturedQuote = null;
        var vm = BuildVm(() => Task.FromResult(ReplyWithResumeAck(confirmed: true, quote: "yes I'm ready")));
        vm.AcknowledgeResumeCallback = q => capturedQuote = q;

        vm.InputText = "I'm okay, let's keep going";
        vm.SendCommand.Execute(null);

        Assert.Equal("yes I'm ready", capturedQuote);
    }

    [Fact]
    public void ResumeAck_UserConfirmedFalse_DoesNotInvokeCallback()
    {
        bool resumed = false;
        var vm = BuildVm(() => Task.FromResult(ReplyWithResumeAck(confirmed: false, quote: "not yet")));
        vm.AcknowledgeResumeCallback = _ => resumed = true;

        vm.InputText = "I'm still feeling off";
        vm.SendCommand.Execute(null);

        Assert.False(resumed);
    }

    [Fact]
    public void NoResumeAckBlock_DoesNotInvokeCallback()
    {
        bool resumed = false;
        var vm = BuildVm(() => Task.FromResult(new NluResult { Intent = "Unknown", Response = "hi", RawModelOutput = "hi there" }));
        vm.AcknowledgeResumeCallback = _ => resumed = true;

        vm.InputText = "hello";
        vm.SendCommand.Execute(null);

        Assert.False(resumed);
    }
}
