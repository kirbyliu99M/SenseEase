using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-1 Wave 1 (H1 / H4): the clinical safety floor is the line-1 intercept of
/// <c>ChatbotViewModel.SendAsync</c>, so a crisis message can never be routed to
/// the profile-picker or a profile-switch proposal Card — even when the same
/// message also carries a lexicon trigger (switch / RESILIENT). The floor returns
/// BEFORE the lexicon fallback and before any LLM dispatch.
/// </summary>
public class ChatbotViewModelSafetyFloorTests
{
    private sealed class StubChatbotService : IChatbotService
    {
        public bool IsModelAvailable => false;
        public bool IsModelLoaded => false;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult(new NluResult { Intent = "Unknown", Response = "LLM-SHOULD-NOT-RUN" });
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => System.Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm(HabituationProfileSnapshot? snapshot)
    {
        var vm = new ChatbotViewModel(
            chatbot: new StubChatbotService(),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);
        vm.LoadHabituationSnapshot = () => snapshot;
        return vm;
    }

    [Theory]
    [InlineData("switch profile to RESILIENT — I want to die")]
    [InlineData("把 profile 換成 RESILIENT，我想死")]
    public void SendAsync_CombinedSelfHarmAndProfileTrigger_CrisisOnly_NoProposal(string input)
    {
        var vm = BuildVm(new HabituationProfileSnapshot { Profile = "SENSITIVE", CurrentVimsTier = 3 });

        int proposals = 0;
        vm.ProposalReceived += (_, _) => proposals++;

        vm.InputText = input;
        vm.SendCommand.Execute(null);   // crisis branch completes synchronously

        Assert.Equal(0, proposals);

        bool crisisShown = false;
        bool llmReplyShown = false;
        foreach (var m in vm.Messages)
        {
            if (m.Role != ChatRole.Assistant) continue;
            if (m.Content == SafetyFloor.CrisisResponseEn || m.Content == SafetyFloor.CrisisResponseZh)
                crisisShown = true;
            if (m.Content == "LLM-SHOULD-NOT-RUN")
                llmReplyShown = true;
        }

        Assert.True(crisisShown, "crisis-line response must be shown");
        Assert.False(llmReplyShown, "the LLM must never be dispatched for a crisis message");
    }
}
