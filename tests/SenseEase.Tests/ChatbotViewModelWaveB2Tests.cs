using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.Localization;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Wave B2 (final) — ChatbotViewModel pieces owned by this slice:
/// <list type="bullet">
///   <item>NE6 — the user-facing Cancel affordance. <c>CancelGenerationCommand</c> is
///   only executable while a turn is generating; invoking it cancels the in-flight CTS so
///   the awaited model call unwinds through the existing OperationCanceledException catch
///   and the localized cancelled-state copy renders.</item>
///   <item>UX-19 — the welcome bubble is a single-language Pick chosen at DISPLAY time
///   (was a stacked zh+en const), and it re-picks after Clear() under the active language.</item>
/// </list>
///
/// <para>Same headless harness shape as ChatbotViewModelInjectionHardeningTests: the LLM
/// stub returns an already-completed/faulted task and reports IsModelLoaded=true so
/// SendAsync runs synchronously on the test thread (no dispatcher pumping needed).</para>
/// </summary>
[Collection("TestDataRoot")]
public class ChatbotViewModelWaveB2Tests
{
    public ChatbotViewModelWaveB2Tests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(ChatbotViewModelWaveB2Tests));
    }

    /// <summary>LLM stub whose SendAsync delegate receives the cancellation token so a
    /// test can observe / react to cancellation. IsModelLoaded=true → synchronous render
    /// path (no async fake-stream).</summary>
    private sealed class StubChatbotService : IChatbotService
    {
        private readonly Func<CancellationToken, Task<NluResult>> _send;
        public StubChatbotService(Func<CancellationToken, Task<NluResult>> send) => _send = send;
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => true;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default) => _send(ct);
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(Func<CancellationToken, Task<NluResult>> responder)
        => new ChatbotViewModel(
            chatbot: new StubChatbotService(responder),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);

    private static void WithLanguage(UILanguage lang, Action body)
    {
        var prior = LocalizationManager.Current.Language;
        try { LocalizationManager.Current.Language = lang; body(); }
        finally { LocalizationManager.Current.Language = prior; }
    }

    // ── NE6: Cancel affordance ───────────────────────────────────────────────

    [Fact]
    public void NE6_CancelGenerationCommand_CannotExecute_WhenIdle()
    {
        // Nothing in flight → the Cancel button collapses (CanExecute false).
        var vm = BuildVm(ct => Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" }));
        Assert.False(vm.CancelGenerationCommand.CanExecute(null));
    }

    [Fact]
    public void NE6_CancelGeneration_Idle_IsHarmlessNoOp()
    {
        // Clicking Cancel with nothing generating must never throw (defensive against a
        // double-click landing after the turn already finished).
        var vm = BuildVm(ct => Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" }));
        vm.CancelGenerationCommand.Execute(null); // no exception
        Assert.False(vm.IsGenerating);
    }

    [Fact]
    public void NE6_CancelGenerationCommand_CancelsInFlightTurn_RendersCancelledCopy()
    {
        ChatbotViewModel? vm = null;
        bool tokenSeenCancellable = false;
        bool canExecuteWhileGenerating = false;

        // The stub runs DURING the in-flight turn: it captures the live token, confirms
        // the Cancel command is executable, fires it (cancelling the linked CTS), then
        // returns a cancelled task so the awaited call faults like a real cancellation.
        vm = BuildVm(ct =>
        {
            tokenSeenCancellable = ct.CanBeCanceled;
            canExecuteWhileGenerating = vm!.CancelGenerationCommand.CanExecute(null);
            vm.CancelGenerationCommand.Execute(null); // user clicks Cancel mid-turn
            Assert.True(ct.IsCancellationRequested);   // the click reached the linked token
            return Task.FromCanceled<NluResult>(ct);
        });

        vm.InputText = "how should I set things up?";
        vm.SendCommand.Execute(null);

        Assert.True(tokenSeenCancellable);          // a cancellable token was threaded in (not None)
        Assert.True(canExecuteWhileGenerating);     // the Cancel button was live during the turn
        // The cancelled turn renders the EXISTING localized cancelled-state copy, never a
        // raw "[Error] …", and clears the typing indicator.
        Assert.DoesNotContain(vm.Messages, m => m.Content.Contains("[Error]"));
        Assert.Contains(vm.Messages, m => m.Role == ChatRole.Assistant
            && (m.Content.Contains("請再試一次") || m.Content.Contains("please try again")));
        Assert.DoesNotContain(vm.Messages, m => m.IsTyping);
        Assert.False(vm.IsGenerating);
        // Back to idle → the Cancel affordance collapses again.
        Assert.False(vm.CancelGenerationCommand.CanExecute(null));
    }

    // ── UX-19: welcome bubble localizes per language, re-picks after Clear ────

    [Fact]
    public void UX19_Welcome_IsSingleLanguage_PerActiveLanguage()
    {
        // EN: the welcome bubble is the English half only — no stacked zh line.
        WithLanguage(UILanguage.English, () =>
        {
            var vm = BuildVm(ct => Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" }));
            var welcome = vm.Messages.First(m => m.Role == ChatRole.Assistant).Content;
            Assert.Contains("SenseEase AI assistant", welcome);
            Assert.DoesNotContain("您好", welcome);   // no Chinese half bleeding through
        });

        // zh: the welcome bubble is the Chinese half only.
        WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            var vm = BuildVm(ct => Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" }));
            var welcome = vm.Messages.First(m => m.Role == ChatRole.Assistant).Content;
            Assert.Contains("SenseEase AI 助理", welcome);
            Assert.DoesNotContain("Hi — I'm", welcome); // no English half
        });
    }

    [Fact]
    public void UX19_Welcome_RePicks_OnClear_UnderActiveLanguage()
    {
        // Construct under zh, then flip to EN and Clear() — the re-shown welcome must
        // re-pick in the NOW-active language (it is never persisted, so it is safe to
        // localize at display time).
        ChatbotViewModel? vm = null;
        WithLanguage(UILanguage.ChineseTraditional, () =>
        {
            vm = BuildVm(ct => Task.FromResult(new NluResult { Intent = "Unknown", Response = "ok" }));
        });

        WithLanguage(UILanguage.English, () =>
        {
            vm!.ClearCommand.Execute(null);
            var welcome = vm.Messages.Single().Content; // Clear leaves only the welcome bubble
            Assert.Contains("SenseEase AI assistant", welcome);
            Assert.DoesNotContain("您好", welcome);
        });
    }
}
