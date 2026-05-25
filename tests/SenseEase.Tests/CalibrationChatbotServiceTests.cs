using System.IO;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

public class CalibrationChatbotServiceTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sensease-cal-tests-{Guid.NewGuid():N}.jsonl");

    private static CalibrationSensorData Sensor() => new(45.0, 2, 22.0);

    private sealed class StubLlm : ICalibrationLlmClient
    {
        public readonly List<(string SystemPrompt, List<ChatMessage> History)> Calls = new();
        public Queue<string?> Replies { get; } = new();

        public Task<string?> SendChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            Calls.Add((systemPrompt, history.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }).ToList()));
            string? reply = Replies.Count > 0 ? Replies.Dequeue() : "OK";
            return Task.FromResult(reply);
        }
    }

    private static string DeltaReply(int? q3Intensity, bool isComplete, double[]? confidence = null)
    {
        string conf = confidence is null ? "null" : "[" + string.Join(",", confidence) + "]";
        string q3 = q3Intensity is null ? "null" : q3Intensity.Value.ToString();
        return @$"Sure, noting that.
[CALIBRATION_DELTA]
{{
  ""delta"": {{ ""q3_intensity"": {q3} }},
  ""confidence"": {conf},
  ""is_complete"": {(isComplete ? "true" : "false")}
}}
[/CALIBRATION_DELTA]";
    }

    // ── Turn counting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TurnCount_IncrementsPerExchange()
    {
        var llm = new StubLlm();
        for (int i = 0; i < 3; i++) llm.Replies.Enqueue(DeltaReply(null, false));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        await svc.StartAsync(null, Sensor());
        Assert.Equal(0, svc.TurnCount);

        await svc.SendUserMessageAsync("first");
        Assert.Equal(1, svc.TurnCount);
        await svc.SendUserMessageAsync("second");
        Assert.Equal(2, svc.TurnCount);
        await svc.SendUserMessageAsync("third");
        Assert.Equal(3, svc.TurnCount);
    }

    [Fact]
    public async Task IsActive_FlipsCorrectly()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, true, new[] { 0.8, 0.8, 0.8, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());
        Assert.False(svc.IsActive);

        await svc.StartAsync(null, Sensor());
        Assert.True(svc.IsActive);

        await svc.SendUserMessageAsync("done");
        Assert.False(svc.IsActive); // completed → session ended
    }

    // ── Convergence injection at turn 8 ───────────────────────────────────────

    [Fact]
    public async Task Convergence_InjectedOnTurn9Call_WhenStillNotComplete()
    {
        var llm = new StubLlm();
        for (int i = 0; i < 9; i++) llm.Replies.Enqueue(DeltaReply(3, false, new[] { 0.8, 0.8, 0.8, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        await svc.StartAsync(null, Sensor());
        for (int i = 0; i < 8; i++) await svc.SendUserMessageAsync($"turn {i + 1}");

        // The 9th send is the one that should carry the convergence directive.
        Assert.Equal(8, svc.TurnCount);
        await svc.SendUserMessageAsync("turn 9");

        // Inspect the LLM call history — the 9th call's history should include
        // a convergence reminder message.
        Assert.Equal(9, llm.Calls.Count);
        var ninthHistory = llm.Calls[8].History;
        bool hasConvergence = ninthHistory.Any(m =>
            m.Content.Contains("SYSTEM REMINDER", StringComparison.OrdinalIgnoreCase)
            || m.Content.Contains("系統提示", StringComparison.Ordinal)
            || m.Content.Contains("收斂", StringComparison.Ordinal)
            || m.Content.Contains("converge", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasConvergence, "Convergence reminder was not injected on the 9th call");
    }

    [Fact]
    public async Task HardStop_FiresCompletedAtTurn9_EvenIfModelRefusesToConverge()
    {
        var llm = new StubLlm();
        for (int i = 0; i < 9; i++) llm.Replies.Enqueue(DeltaReply(3, false, new[] { 0.8, 0.8, 0.8, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        int completedFires = 0;
        bool isCompleteFlag = true;
        svc.Completed += (_, ic) => { completedFires++; isCompleteFlag = ic; };

        await svc.StartAsync(null, Sensor());
        for (int i = 0; i < 9; i++) await svc.SendUserMessageAsync($"turn {i + 1}");

        Assert.Equal(1, completedFires);
        Assert.False(isCompleteFlag);
        Assert.False(svc.IsActive);
    }

    // ── Safety floor ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SafetyFloor_ChineseSuicidePhrase_FiresAndShortCircuits()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(3, true, new[] { 0.8, 0.8, 0.8, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        string? safetyMsg = null;
        svc.SafetyFloorTriggered += m => safetyMsg = m;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("我想自殺");

        Assert.NotNull(safetyMsg);
        Assert.Contains("1925", safetyMsg!);
        Assert.Empty(llm.Calls); // LLM was NOT called
        Assert.False(svc.IsActive); // EndSession fired
    }

    [Fact]
    public async Task SafetyFloor_EnglishPhrase_FiresAndShortCircuits()
    {
        var llm = new StubLlm();
        using var svc = new CalibrationChatbotService(llm, TempPath());
        string? safetyMsg = null;
        svc.SafetyFloorTriggered += m => safetyMsg = m;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("I want to die");

        Assert.NotNull(safetyMsg);
        Assert.Contains("988", safetyMsg!);
        Assert.Empty(llm.Calls);
        Assert.False(svc.IsActive);
    }

    [Fact]
    public async Task SafetyFloor_FalsePositiveGuard_AmbiguousChinese_DoesNotTrigger()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, false, new[] { 0.6, 0.6, 0.6, 0.6, 0.6 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        bool fired = false;
        svc.SafetyFloorTriggered += _ => fired = true;

        await svc.StartAsync(null, Sensor());
        // "暈到不想活動" = dizzy to the point of not wanting to move — must NOT trip
        // the "不想活" substring guard.
        await svc.SendUserMessageAsync("暈到不想活動");

        Assert.False(fired);
        Assert.Single(llm.Calls); // LLM was called normally
    }

    [Fact]
    public async Task SafetyFloor_DirectMatch_VariantPhrases()
    {
        // Each variant in isolation should trip.
        string[] zhVariants = { "自殘", "我想結束生命", "想跳樓", "我想死了" };
        foreach (var phrase in zhVariants)
        {
            var llm = new StubLlm();
            using var svc = new CalibrationChatbotService(llm, TempPath());
            bool fired = false;
            svc.SafetyFloorTriggered += _ => fired = true;
            await svc.StartAsync(null, Sensor());
            await svc.SendUserMessageAsync(phrase);
            Assert.True(fired, $"Should have tripped on: {phrase}");
        }
    }

    // ── Language detection ────────────────────────────────────────────────────

    [Fact]
    public async Task LanguageDetection_ChineseFirstMessage_UsesZhPrompt()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, false, new[] { 0.6, 0.6, 0.6, 0.6, 0.6 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("剛剛覺得有點頭暈");

        Assert.Single(llm.Calls);
        string sys = llm.Calls[0].SystemPrompt;
        // zh prompt opens with "你是 SenseEase 校準對話助手"
        Assert.Contains("校準對話助手", sys, StringComparison.Ordinal);
        // Should NOT contain the en opening
        Assert.DoesNotContain("You are SenseEase's calibration dialog assistant", sys, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageDetection_EnglishFirstMessage_UsesEnPrompt()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, false, new[] { 0.6, 0.6, 0.6, 0.6, 0.6 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("I felt a bit dizzy");

        Assert.Single(llm.Calls);
        string sys = llm.Calls[0].SystemPrompt;
        Assert.Contains("You are SenseEase's calibration dialog assistant", sys, StringComparison.Ordinal);
        Assert.DoesNotContain("校準對話助手", sys, StringComparison.Ordinal);
    }

    // ── Q3 confidence fallback ────────────────────────────────────────────────

    [Fact]
    public async Task RequestQ3Fallback_Fires_WhenQ3IntensityMissing()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(q3Intensity: null, isComplete: true, confidence: new[] { 0.8, 0.8, 0.8, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        bool fallbackFired = false;
        svc.RequestQ3Fallback += () => fallbackFired = true;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("dunno");

        Assert.True(fallbackFired);
    }

    [Fact]
    public async Task RequestQ3Fallback_Fires_WhenQ3ConfidenceBelowThreshold()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(q3Intensity: 3, isComplete: true,
            confidence: new[] { 0.8, 0.8, 0.5, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        bool fallbackFired = false;
        svc.RequestQ3Fallback += () => fallbackFired = true;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("done");

        Assert.True(fallbackFired);
    }

    [Fact]
    public async Task RequestQ3Fallback_DoesNotFire_WhenQ3SetAndConfident()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(q3Intensity: 3, isComplete: true,
            confidence: new[] { 0.8, 0.8, 0.85, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        bool fallbackFired = false;
        svc.RequestQ3Fallback += () => fallbackFired = true;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("done");

        Assert.False(fallbackFired);
    }

    // ── Completed event firing semantics ──────────────────────────────────────

    [Fact]
    public async Task Completed_FiresOncePerSession()
    {
        var llm = new StubLlm();
        // Multiple is_complete=true responses in a row — Completed must fire ONCE.
        llm.Replies.Enqueue(DeltaReply(3, true, new[] { 0.8, 0.8, 0.85, 0.8, 0.8 }));
        llm.Replies.Enqueue(DeltaReply(3, true, new[] { 0.8, 0.8, 0.85, 0.8, 0.8 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        int completedFires = 0;
        svc.Completed += (_, _) => completedFires++;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("done");
        // Session should have ended; subsequent sends are no-ops.
        await svc.SendUserMessageAsync("done again");

        Assert.Equal(1, completedFires);
    }

    // ── Transcript persistence ────────────────────────────────────────────────

    [Fact]
    public async Task Transcript_AppendsToInjectedPath()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, false, new[] { 0.7, 0.7, 0.7, 0.7, 0.7 }));
        string path = TempPath();
        try
        {
            using var svc = new CalibrationChatbotService(llm, path);
            await svc.StartAsync(null, Sensor());
            await svc.SendUserMessageAsync("hello there");

            Assert.True(File.Exists(path));
            string contents = File.ReadAllText(path);
            Assert.Contains("hello there", contents);
            Assert.Contains("User", contents);
            Assert.Contains("Assistant", contents);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── DeltaParsed event ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeltaParsed_FiresWithParsedDelta()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(3, false, new[] { 0.7, 0.7, 0.7, 0.7, 0.7 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        CalibrationResponses? captured = null;
        svc.DeltaParsed += d => captured = d;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("3 out of 5");

        Assert.NotNull(captured);
        Assert.Equal(3, captured!.Q3Intensity);
    }

    // ── AssistantMessageReceived has delta stripped ──────────────────────────

    [Fact]
    public async Task AssistantMessage_HasDeltaBlockStripped()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(3, false, new[] { 0.7, 0.7, 0.7, 0.7, 0.7 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        string? received = null;
        svc.AssistantMessageReceived += m => received = m;

        await svc.StartAsync(null, Sensor());
        await svc.SendUserMessageAsync("ok");

        Assert.NotNull(received);
        Assert.DoesNotContain("CALIBRATION_DELTA", received!);
        Assert.Contains("Sure", received);
    }

    // ── Snapshot context rendering ────────────────────────────────────────────

    [Fact]
    public async Task SystemPrompt_FirstRun_RendersFirstRunMarker()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, false, new[] { 0.6, 0.6, 0.6, 0.6, 0.6 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        await svc.StartAsync(previousSnapshot: null, Sensor());
        await svc.SendUserMessageAsync("hi");

        string sys = llm.Calls[0].SystemPrompt;
        // En default since "hi" has no Han chars.
        Assert.Contains("first calibration session", sys, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemPrompt_WithPriorSnapshot_RendersPrior()
    {
        var llm = new StubLlm();
        llm.Replies.Enqueue(DeltaReply(2, false, new[] { 0.6, 0.6, 0.6, 0.6, 0.6 }));
        using var svc = new CalibrationChatbotService(llm, TempPath());

        var snap = new HabituationProfileSnapshot
        {
            Profile = "TYPICAL",
            ProfileSource = "calibration_demo",
            HabituationLevel = 25.5,
            HabituationOffsetCapPp = 40,
            CurrentVimsTier = 4,
            UpdatedAtIso = "2026-05-20T10:00:00Z",
        };

        await svc.StartAsync(previousSnapshot: snap, Sensor());
        await svc.SendUserMessageAsync("hi again");

        string sys = llm.Calls[0].SystemPrompt;
        Assert.Contains("TYPICAL", sys, StringComparison.Ordinal);
        Assert.Contains("calibration_demo", sys, StringComparison.Ordinal);
    }

    // ── Inactive guards ───────────────────────────────────────────────────────

    [Fact]
    public async Task Send_BeforeStart_IsNoOp()
    {
        var llm = new StubLlm();
        using var svc = new CalibrationChatbotService(llm, TempPath());

        await svc.SendUserMessageAsync("hello");
        Assert.Empty(llm.Calls);
        Assert.Equal(0, svc.TurnCount);
    }

    [Fact]
    public async Task Send_EmptyMessage_IsNoOp()
    {
        var llm = new StubLlm();
        using var svc = new CalibrationChatbotService(llm, TempPath());
        await svc.StartAsync(null, Sensor());

        await svc.SendUserMessageAsync(string.Empty);
        await svc.SendUserMessageAsync("   ");
        Assert.Empty(llm.Calls);
    }
}
