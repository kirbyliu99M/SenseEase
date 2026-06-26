using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pause-release (2026-06-26): on PAUSE the chatbot VM must hand the host-release call through (so the
/// ~3 GB LLM host frees instead of staying resident — the OV NPU host has idle-unload disabled). Two contracts:
///   • <see cref="ChatbotViewModel.PauseAndReleaseModel"/> invokes the injected release callback immediately;
///   • when a pause lands MID-TURN (the release would have been skipped because the turn held the request lock),
///     the <c>SendAsync</c> finally RE-FIRES the release once the turn ends — and a non-paused turn does NOT.
/// The release CALLBACK is the production <c>LocalLlmChatbotService.ReleaseForPause</c> (its own ownership +
/// cache-safety gating is unit-tested in <see cref="LocalLlmChatbotServiceExternalBackendTests"/>); here we pin
/// only the VM's wiring + the deferral contract via a counting stand-in.</summary>
[Collection("TestDataRoot")]
public class ChatbotViewModelPauseReleaseTests
{
    public ChatbotViewModelPauseReleaseTests()
        => TestDataRootFixture.UseClassSubdir(nameof(ChatbotViewModelPauseReleaseTests));

    private sealed class StubChatbotService : IChatbotService
    {
        private readonly Func<NluResult> _send;
        public StubChatbotService(Func<NluResult> send) => _send = send;
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => true;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default) => Task.FromResult(_send());
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(Func<NluResult> responder, Action release)
        => new ChatbotViewModel(
            chatbot: new StubChatbotService(responder),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false,
            releaseModelForPause: release);

    [Fact]
    public void PauseAndReleaseModel_InvokesReleaseCallback_AndGatesSend()
    {
        int releases = 0;
        var vm = BuildVm(() => new NluResult { Intent = "Unknown", Response = "ok" }, () => releases++);

        vm.PauseAndReleaseModel();

        Assert.True(vm.IsPaused);                              // new requests gated
        Assert.Equal(1, releases);                            // host-release call handed through
        Assert.False(vm.SendCommand.CanExecute(null));        // SendCommand disabled while paused
    }

    [Fact]
    public void ResumeFromPause_UngatesSend()
    {
        var vm = BuildVm(() => new NluResult { Intent = "Unknown", Response = "ok" }, () => { });
        vm.PauseAndReleaseModel();

        vm.ResumeFromPause(warmReload: false);                // lazy reload — host re-loads on next message

        Assert.False(vm.IsPaused);
    }

    [Fact]
    public void PauseMidTurn_DefersRelease_RefiresAtTurnEnd()
    {
        if (File.Exists(AppConfig.ChatHistoryPath)) File.Delete(AppConfig.ChatHistoryPath);

        int releases = 0;
        ChatbotViewModel? vm = null;
        // The pause lands WHILE the turn is in flight: the responder (invoked from inside SendAsync) flips
        // IsPaused directly — as a real concurrent ApplyPowerSave(true) would — WITHOUT going through
        // PauseAndReleaseModel, so the only release path under test is the SendAsync finally's deferral.
        vm = BuildVm(
            () => { vm!.IsPaused = true; return new NluResult { Intent = "Unknown", Response = "ok" }; },
            () => releases++);

        vm.InputText = "hi";
        vm.SendCommand.Execute(null);                         // synchronous in the headless harness

        Assert.True(vm.IsPaused);
        Assert.Equal(1, releases);                            // released once, at turn-end (deferred)
    }

    [Fact]
    public void NormalTurn_NotPaused_DoesNotRelease()
    {
        if (File.Exists(AppConfig.ChatHistoryPath)) File.Delete(AppConfig.ChatHistoryPath);

        int releases = 0;
        var vm = BuildVm(() => new NluResult { Intent = "Unknown", Response = "ok" }, () => releases++);

        vm.InputText = "hi";
        vm.SendCommand.Execute(null);

        Assert.False(vm.IsPaused);
        Assert.Equal(0, releases);                            // no pause → the finally never releases
    }
}
