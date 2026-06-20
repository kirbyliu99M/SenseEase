using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>NEW-B14 (W-B 2026-06-20): a held (rest-advice) turn must render exactly ONE honest reply — the
/// rest advice — and must NOT carry the false "I eased it" claim that the upstream compliance scrub blessed
/// (with actuated=true) before the VM zeroed the deltas to hold. With the LLM stub leaving
/// <c>CompleteShortAsync</c> at the interface default (returns null, history-free), the held turn falls back
/// to the deterministic honest rest line — which is what this test pins.
///
/// <para>Harness mirrors the other ChatbotViewModel tests: <c>IsModelLoaded=true</c> + a completed-task
/// responder ⇒ the turn renders synchronously, and the reply is persisted to the seam-isolated
/// chat_history.jsonl before the async fake-stream, so the on-disk transcript is a deterministic
/// assertion target.</para></summary>
[Collection("TestDataRoot")]
public class ChatbotViewModelRestAdviceTests
{
    public ChatbotViewModelRestAdviceTests()
        => TestDataRootFixture.UseClassSubdir(nameof(ChatbotViewModelRestAdviceTests));

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
        // CompleteShortAsync left at the interface default (returns null) ⇒ the held turn uses the
        // deterministic honest rest line, with no network call.
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(Func<NluResult> responder)
        => new ChatbotViewModel(
            chatbot: new StubChatbotService(responder),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);

    [Fact]
    public void RepeatedDiscomfort_HeldTurn_ReplacesFalseClaim_WithHonestRestAdvice()
    {
        var priorLang = LocalizationManager.Current.Language;
        LocalizationManager.Current.Language = UILanguage.English;
        try
        {
            string historyPath = AppConfig.ChatHistoryPath;
            if (File.Exists(historyPath)) File.Delete(historyPath);

            // The model both CLAIMS an action and sets a real delta — exactly the reply the upstream
            // compliance scrub would bless (actuated=true) before the VM zeroes the deltas on a held turn.
            var vm = BuildVm(() => new NluResult
            {
                Intent = "ReportDiscomfort",
                MaskDepthDelta = 0.2,
                Response = "I've eased the motion for you.",
            });

            // Three discomfort reports inside the 5-min window → the 3rd trips the rest-advice hold.
            for (int i = 0; i < 3; i++) { vm.InputText = "still really dizzy"; vm.SendCommand.Execute(null); }

            var persisted = new FileChatHistoryStore(historyPath).Load();
            string lastAssistant = persisted.Last(m => m.Role == ChatRole.Assistant).Content;

            // The held (3rd) turn's single reply is the honest rest advice — NOT the false action claim.
            Assert.Contains("rest your eyes", lastAssistant, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eased the motion", lastAssistant, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LocalizationManager.Current.Language = priorLang;
        }
    }

    [Fact]
    public void StrongerRequest_AtTightestLimit_ReplacesFalseClaim_WithAtStrongestLine()
    {
        // NEW-B13: a "make it stronger" that the engine clamps to a no-op (already at the limit) must NOT
        // ship the model's "I made it stronger" claim — it's replaced by an honest at-strongest line.
        var priorLang = LocalizationManager.Current.Language;
        LocalizationManager.Current.Language = UILanguage.English;
        try
        {
            string historyPath = AppConfig.ChatHistoryPath;
            if (File.Exists(historyPath)) File.Delete(historyPath);

            int fovDelta = -100; // turn 1 saturates the FOV offset to its tightest clamp (-60)
            var vm = BuildVm(() => new NluResult
            {
                Intent = "Unknown",
                FovDelta = fovDelta,
                Response = "I've made it stronger for you.",
            });

            vm.InputText = "much tighter please"; vm.SendCommand.Execute(null); // applies → reaches the limit
            fovDelta = -5;
            vm.InputText = "even stronger";        vm.SendCommand.Execute(null); // already at the limit → no-op

            var persisted = new FileChatHistoryStore(historyPath).Load();
            string lastAssistant = persisted.Last(m => m.Role == ChatRole.Assistant).Content;

            // The at-limit turn replaced the false claim with an honest at-strongest line.
            Assert.Contains("already at its strongest", lastAssistant, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("made it stronger", lastAssistant, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LocalizationManager.Current.Language = priorLang;
        }
    }
}
