using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-1 Wave 1 (H1 / H4): the clinical safety floor is the line-1 intercept of
/// <c>ChatbotViewModel.SendAsync</c>, so a crisis message can never be routed to
/// the profile-picker or a profile-switch proposal Card — even when the same
/// message also carries a lexicon trigger (switch / RESILIENT). The floor returns
/// BEFORE the lexicon fallback and before any LLM dispatch.
/// </summary>
[Collection("TestDataRoot")]
public class ChatbotViewModelSafetyFloorTests
{
    // CW-9 (TEST-01/14): the real data-root seam. The production ChatbotViewModel
    // hard-wires `new FileChatHistoryStore(AppConfig.ChatHistoryPath)` in its
    // constructor, so simply constructing a VM in a test appends to whatever
    // ChatHistoryPath resolves to. We point AppConfig.DataRootOverride at a unique
    // per-class temp subdir BEFORE the VM is built, so every persist lands there and
    // never touches the developer's real %LOCALAPPDATA%\SenseEase\chat_history.jsonl.
    // (The previous LOCALAPPDATA env-var trick was a silent no-op — GetFolderPath
    // ignores it.) This per-test ctor also re-points the override fresh each test so
    // the H2 boundary test below reads a clean file.
    private readonly string _dataRoot;

    public ChatbotViewModelSafetyFloorTests()
    {
        _dataRoot = TestDataRootFixture.UseClassSubdir(nameof(ChatbotViewModelSafetyFloorTests));
    }

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

    // ── CW-9 (TEST-14): the REAL H2 persist-boundary test ───────────────────
    // This replaces the old CrisisPersist_StoresRedactedPlaceholder_NotRawText,
    // which was a tautology: it called store.Append(RedactedPlaceholder) itself and
    // then asserted the placeholder it had just written — the VM was never exercised,
    // so it could not have caught a regression that persisted the RAW user text.
    //
    // The test below drives the REAL ChatbotViewModel through its crisis branch with
    // the data-root seam active, then READS the on-disk temp chat_history.jsonl and
    // asserts (a) the persisted USER turn is exactly SafetyFloor.RedactedPlaceholder
    // and (b) the raw crisis phrase appears NOWHERE in the file (H2). This is true
    // end-to-end coverage: the VM's PersistMessage path is what writes the file.

    [Theory]
    [InlineData("switch profile to RESILIENT — I want to die", "want to die")]
    [InlineData("把 profile 換成 RESILIENT，我想死", "我想死")]
    public void CrisisPersist_RealVm_WritesRedactedPlaceholder_RawPhraseAbsentFromFile(
        string rawUserText, string rawFragment)
    {
        // The floor must actually trip for this raw input (guards against the
        // phrase lexicon drifting out from under the test).
        Assert.True(SafetyFloor.Trips(rawUserText, out _));

        string historyPath = AppConfig.ChatHistoryPath;
        // Seam sanity: the VM must be writing into THIS test's temp subdir, not the
        // developer's real %LOCALAPPDATA% (the whole point of CW-9).
        Assert.StartsWith(_dataRoot, historyPath, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(historyPath)) File.Delete(historyPath);

        var vm = BuildVm(new HabituationProfileSnapshot { Profile = "SENSITIVE", CurrentVimsTier = 3 });

        vm.InputText = rawUserText;
        vm.SendCommand.Execute(null);   // crisis branch persists synchronously

        // The VM actually wrote the transcript to the seam-isolated temp path.
        Assert.True(File.Exists(historyPath), "the crisis branch must persist the transcript on disk");

        var persisted = new FileChatHistoryStore(historyPath).Load();

        // The persisted USER turn is the redacted placeholder, exactly — never the
        // raw text the user typed.
        var userTurn = persisted.First(m => m.Role == ChatRole.User);
        Assert.Equal(SafetyFloor.RedactedPlaceholder, userTurn.Content);
        Assert.NotEqual(rawUserText, userTurn.Content);

        // The persisted ASSISTANT turn is a fixed, non-PII crisis line.
        var assistantTurn = persisted.First(m => m.Role == ChatRole.Assistant);
        Assert.True(
            assistantTurn.Content == SafetyFloor.CrisisResponseEn
            || assistantTurn.Content == SafetyFloor.CrisisResponseZh,
            "the persisted assistant turn must be a fixed crisis-line response");

        // H2: the raw trigger phrase must appear NOWHERE in the on-disk file — not
        // in the user turn, not smuggled anywhere else. Read the raw bytes/text and
        // scan the whole thing, not just the parsed messages.
        string rawFileText = File.ReadAllText(historyPath);
        Assert.DoesNotContain(rawFragment, rawFileText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RESILIENT", rawFileText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("switch profile to RESILIENT — I want to die")]
    [InlineData("把 profile 換成 RESILIENT，我想死")]
    public void CrisisLiveTranscript_ShowsRawUserText_WhilePersistRedacts(string rawUserText)
    {
        var vm = BuildVm(new HabituationProfileSnapshot { Profile = "SENSITIVE", CurrentVimsTier = 3 });

        vm.InputText = rawUserText;
        vm.SendCommand.Execute(null);   // crisis branch completes synchronously

        // The LIVE transcript still shows the user's own words (UX is preserved —
        // only the on-disk record is redacted).
        bool rawUserBubbleShown = false;
        foreach (var m in vm.Messages)
        {
            if (m.Role == ChatRole.User && m.Content == rawUserText)
                rawUserBubbleShown = true;
        }
        Assert.True(rawUserBubbleShown, "the live user bubble must show the raw text");

        // The placeholder that WOULD be persisted is distinct from the raw text the
        // bubble shows — the two must never collapse to the same string, or
        // redaction would be a no-op.
        Assert.NotEqual(rawUserText, SafetyFloor.RedactedPlaceholder);
    }

    [Fact]
    public void RedactedPlaceholder_IsNonEmpty_AndLeaksNoTriggerText()
    {
        // Invariant guard on the placeholder constant itself: it must be a stable,
        // non-empty sentinel that contains none of the trigger vocabulary it stands
        // in for. (Robust invariant — not pinned to the exact bracketed string, so
        // it survives a benign wording change while still catching a regression that
        // accidentally let raw text leak into the constant.)
        Assert.False(string.IsNullOrWhiteSpace(SafetyFloor.RedactedPlaceholder));
        Assert.DoesNotContain("want to die", SafetyFloor.RedactedPlaceholder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suicide", SafetyFloor.RedactedPlaceholder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("我想死", SafetyFloor.RedactedPlaceholder, StringComparison.Ordinal);

        // The placeholder must itself NOT trip the floor — otherwise reloading a
        // redacted transcript line would re-fire the crisis path on stored data.
        Assert.False(SafetyFloor.Trips(SafetyFloor.RedactedPlaceholder, out _));
    }
}
