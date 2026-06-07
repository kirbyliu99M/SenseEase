using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UM4 (User Model sprint): the batch retrospective analysis pipeline. Drives
/// UserModelRetrospectiveAnalyzer with a fake LLM + real stores on temp files and
/// pins: extract→write→advance, nothing-new (no LLM call), LLM-unavailable (index
/// NOT advanced), analysed-but-empty (index advanced), and the 100-turn cap.
/// </summary>
public class UserModelRetrospectiveAnalyzerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _chatPath;
    private readonly string _indexPath;

    public UserModelRetrospectiveAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-um4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _chatPath = Path.Combine(_tempDir, "chat_history.jsonl");
        _indexPath = Path.Combine(_tempDir, "user_model_index.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeLlm : ICalibrationLlmClient
    {
        public string? Response;
        public int Calls { get; private set; }
        public int LastHistoryCount { get; private set; }
        public Task<string?> SendChatAsync(
            string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            Calls++;
            LastHistoryCount = history.Count;
            return Task.FromResult(Response);
        }
    }

    private sealed class FakeProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Snapshot { get; set; } = new HabituationProfileSnapshot();
        public HabituationProfileSnapshot? Load() => Snapshot;
        public void Save(HabituationProfileSnapshot snapshot) => Snapshot = snapshot;
    }

    private void SeedChat(int turns)
    {
        var chat = new FileChatHistoryStore(_chatPath);
        for (int i = 0; i < turns; i++)
            chat.Append(new ChatMessage { Role = ChatRole.User, Content = $"turn {i} content" });
    }

    [Fact]
    public async Task RunAsync_ExtractsObservations_WritesThem_AndAdvancesIndex()
    {
        var chat = new FileChatHistoryStore(_chatPath);
        chat.Append(new ChatMessage { Role = ChatRole.User, Content = "I felt dizzy scrolling long feeds" });
        chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "Noted." });

        var profile = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(profile);
        var index = new UserModelIndexStore(_indexPath);
        var llm = new FakeLlm { Response = "[OBSERVATION_ADD]user gets dizzy when scrolling long feeds[/OBSERVATION_ADD]" };

        var analyzer = new UserModelRetrospectiveAnalyzer(llm, chat, writer, index);
        int written = await analyzer.RunAsync();

        Assert.Equal(1, written);
        Assert.Equal(1, llm.Calls);
        Assert.Equal(2, index.LoadIndex().LastAnalyzedChatTurnIndex); // past both turns

        string month = DateTime.UtcNow.ToString("yyyy-MM");
        Assert.Single(profile.Snapshot!.LongitudinalObservationsV2[month]);
    }

    [Fact]
    public async Task RunAsync_NothingNew_NoLlmCall()
    {
        SeedChat(2);
        var index = new UserModelIndexStore(_indexPath);
        index.SaveIndex(new UserModelIndex { LastAnalyzedChatTurnIndex = 2 }); // already analysed

        var llm = new FakeLlm { Response = "[OBSERVATION_ADD]x[/OBSERVATION_ADD]" };
        var analyzer = new UserModelRetrospectiveAnalyzer(
            llm, new FileChatHistoryStore(_chatPath), new LongitudinalObservationWriter(new FakeProfileStore()), index);

        int written = await analyzer.RunAsync();

        Assert.Equal(0, written);
        Assert.Equal(0, llm.Calls); // no slice → no LLM dispatch
    }

    [Fact]
    public async Task RunAsync_LlmUnavailable_ReturnsNull_DoesNotAdvanceIndex()
    {
        SeedChat(2);
        var index = new UserModelIndexStore(_indexPath);
        var llm = new FakeLlm { Response = null }; // model not loaded / call failed

        var analyzer = new UserModelRetrospectiveAnalyzer(
            llm, new FileChatHistoryStore(_chatPath), new LongitudinalObservationWriter(new FakeProfileStore()), index);

        int written = await analyzer.RunAsync();

        Assert.Equal(0, written);
        Assert.Equal(1, llm.Calls);
        Assert.Equal(0, index.LoadIndex().LastAnalyzedChatTurnIndex); // NOT advanced → retried next run
    }

    [Fact]
    public async Task RunAsync_AnalysedButNoObservations_AdvancesIndex()
    {
        SeedChat(2);
        var index = new UserModelIndexStore(_indexPath);
        var llm = new FakeLlm { Response = "nothing clinically relevant here" }; // non-null, no blocks

        var analyzer = new UserModelRetrospectiveAnalyzer(
            llm, new FileChatHistoryStore(_chatPath), new LongitudinalObservationWriter(new FakeProfileStore()), index);

        int written = await analyzer.RunAsync();

        Assert.Equal(0, written);
        Assert.Equal(2, index.LoadIndex().LastAnalyzedChatTurnIndex); // advanced (slice WAS analysed)
    }

    [Fact]
    public async Task RunAsync_CapsAtMaxTurnsPerRun_LeavesRemainderForNextRun()
    {
        SeedChat(150);
        var index = new UserModelIndexStore(_indexPath);
        var llm = new FakeLlm { Response = "" };

        var analyzer = new UserModelRetrospectiveAnalyzer(
            llm, new FileChatHistoryStore(_chatPath), new LongitudinalObservationWriter(new FakeProfileStore()), index);

        await analyzer.RunAsync();

        Assert.Equal(UserModelRetrospectiveAnalyzer.MaxTurnsPerRun, llm.LastHistoryCount); // capped slice
        Assert.Equal(UserModelRetrospectiveAnalyzer.MaxTurnsPerRun,
                     index.LoadIndex().LastAnalyzedChatTurnIndex); // advanced by exactly the cap
    }
}
