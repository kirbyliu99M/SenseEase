using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 D-21: the daily NL→FMS estimator. Pins that it derives the day's PEAK reported
/// discomfort into an FMS 0-20 ESTIMATE (never a measured PRO), emits nothing on a day with no
/// discomfort (no FMS=0 noise), records via the option-C recorder (structured + Symptom.Estimate
/// mirror), and is idempotent per UTC day.
/// </summary>
public sealed class DailyFmsEstimatorTests : IDisposable
{
    private readonly string _symPath =
        Path.Combine(Path.GetTempPath(), "senseease_dailyfms_test_" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { if (File.Exists(_symPath)) File.Delete(_symPath); } catch { }
        try { if (File.Exists(_symPath + ".tmp")) File.Delete(_symPath + ".tmp"); } catch { }
    }

    private static ChatMessage User(string text, DateTime tsUtc) =>
        new() { Role = ChatRole.User, Content = text, Timestamp = tsUtc };

    private static readonly DateTime Now = new(2026, 6, 14, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    [Fact]
    public void EstimateForDay_TakesPeakDiscomfort_MapsToFms_AndIsEstimateNotMeasured()
    {
        var msgs = new List<ChatMessage>
        {
            User("我有點頭暈", Now.AddHours(-3)),                 // mild discomfort
            User("天啊我非常想吐，超級不舒服", Now.AddHours(-1)),   // extreme discomfort = peak
            User("謝謝你的幫忙", Now.AddMinutes(-10)),            // not discomfort
        };

        var ev = DailyFmsEstimator.EstimateForDay(msgs, Today, Now);

        Assert.NotNull(ev);
        Assert.Equal(SymptomEvent.Kinds.FmsEstimate, ev!.Kind);
        Assert.False(ev.Measured);                          // ESTIMATE, never a PRO
        Assert.Equal(SymptomEvent.Sources.NlDaily, ev.Source);
        Assert.Equal("fms20", ev.Scale);
        Assert.True(ev.Value >= 14, $"extreme discomfort should map high on FMS 0-20, was {ev.Value}");
    }

    [Fact]
    public void EstimateForDay_NoDiscomfort_ReturnsNull_NoFmsZeroNoise()
    {
        var msgs = new List<ChatMessage>
        {
            User("今天很好，謝謝", Now.AddHours(-2)),
            User("把網格調亮一點", Now.AddHours(-1)),  // a command, not discomfort
        };
        Assert.Null(DailyFmsEstimator.EstimateForDay(msgs, Today, Now));
    }

    [Fact]
    public void EstimateForDay_IgnoresOtherDaysAndAssistantTurns()
    {
        var msgs = new List<ChatMessage>
        {
            User("我昨天頭很暈", Now.AddDays(-1)),                 // yesterday — excluded
            new() { Role = ChatRole.Assistant, Content = "我很暈", Timestamp = Now }, // assistant — excluded
        };
        Assert.Null(DailyFmsEstimator.EstimateForDay(msgs, Today, Now));
    }

    [Fact]
    public void RunOnce_RecordsOncePerUtcDay_ThenSkips()
    {
        // Build a chat store with one discomfort message today.
        string chatPath = Path.Combine(Path.GetTempPath(), "senseease_dailyfms_chat_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var chat = new FileChatHistoryStore(chatPath);
            chat.Append(User("我覺得非常暈，很不舒服", Now.AddHours(-1)));

            var store = new FileSymptomEventStore(_symPath);
            var log = new InMemoryEventLogStore();
            var recorder = new SymptomEventRecorder(store, log);
            var est = new DailyFmsEstimator(chat, store, recorder);

            var first = est.RunOnce(Now);
            Assert.NotNull(first);                              // recorded
            Assert.Single(store.ReadAll());
            Assert.Equal("Symptom.Estimate", log.Recent().Single().Category); // mirrored

            var second = est.RunOnce(Now.AddMinutes(5));        // same UTC day
            Assert.Null(second);                                // idempotent — no second record
            Assert.Single(store.ReadAll());
        }
        finally
        {
            try { if (File.Exists(chatPath)) File.Delete(chatPath); } catch { }
        }
    }
}
