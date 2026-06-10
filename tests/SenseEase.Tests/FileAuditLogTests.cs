using System;
using System.IO;
using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>H17: durable JSONL audit logs — FileEventLogStore + FileChatbotActionLog.
/// They drop in for the in-memory stores (same Recent()/Appended behaviour) but add
/// a forensic file trail that survives restarts.</summary>
public class FileAuditLogTests : IDisposable
{
    private readonly string _dir;

    public FileAuditLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "senseease-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void EventLog_ForensicCategory_PersistsToDisk_AndMirrors()
    {
        var store = new FileEventLogStore(_dir);
        EventLogEntry? mirrored = null;
        store.Appended += (_, e) => mirrored = e;

        store.Append(new EventLogEntry { Category = "Habituation.Profile", Message = "profile flipped to RESILIENT" });

        // in-memory window + Appended mirror behave like the in-memory store (M1)
        Assert.Single(store.Recent());
        Assert.NotNull(mirrored);
        Assert.Equal("profile flipped to RESILIENT", mirrored!.Message);

        // durable: the forensic line landed on today's file
        string raw = ReadAllDayFiles("event-log");
        Assert.Contains("profile flipped to RESILIENT", raw);
        Assert.Contains("Habituation.Profile", raw);
    }

    [Fact]
    public void EventLog_NonForensicCategory_NotPersisted_ButStillMirrored()
    {
        var store = new FileEventLogStore(_dir);
        store.Append(new EventLogEntry { Category = "Inference", Message = "ofi tick" });

        Assert.Single(store.Recent());                 // dashboard still sees it
        Assert.Empty(ReadAllDayFiles("event-log"));    // but it is NOT in the forensic file
    }

    [Fact]
    public void ChatbotActionLog_RecordDecline_SurvivesRestart()
    {
        var proposal = new ProfileOverrideProposal(
            OverrideIntent.AdjustSensitivity, "adjust_sensitivity", 1.1, "test", DateTime.UtcNow.AddDays(2));

        var log1 = new FileChatbotActionLog(_dir);
        log1.Record(ChatbotActionKind.Declined, proposal);
        Assert.NotNull(log1.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity));

        // "restart": a fresh instance over the same dir loads the prior decline
        var log2 = new FileChatbotActionLog(_dir);
        Assert.NotNull(log2.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity));
        Assert.Equal(ChatbotActionKind.Declined, log2.Recent()[0].Kind);
        Assert.Equal(OverrideIntent.AdjustSensitivity, log2.Recent()[0].Intent);
    }

    // ── FEAT-09 / LOGIC-07 / SEC-04: daily-cap hardening ─────────────────────

    /// <summary>Fill past the 1 MB/day cap with ordinary durable entries, then assert:
    /// the truncation sentinel is written exactly once; a Safety.* entry still lands
    /// after the cap; a Chatbot.Declined entry still lands after the cap; an ordinary
    /// durable entry after the cap is dropped.</summary>
    [Fact]
    public void EventLog_PastDailyCap_ExemptEntriesLand_OrdinaryDrops_SentinelOnce()
    {
        var store = new FileEventLogStore(_dir);

        // ~512-byte messages so we cross the 1 MB cap in a bounded number of appends.
        string padding = new string('x', 512);
        // 1 MB / ~512B ≈ 2048 lines; overshoot to be safely past the cap.
        for (int i = 0; i < 3000; i++)
            store.Append(new EventLogEntry { Category = "Habituation.Profile", Message = padding });

        // We must now be over the cap.
        Assert.True(DayFileBytes("event-log") >= FileEventLogStore.MaxDailyFileBytes);

        // Two ordinary (non-exempt) entries past the cap: both dropped, but they trigger
        // exactly one sentinel between them.
        store.Append(new EventLogEntry { Category = "Habituation.Profile", Message = "DROP-ME-1" });
        store.Append(new EventLogEntry { Category = "Habituation.Profile", Message = "DROP-ME-2" });

        // Exempt entries past the cap: a Safety.* and the exact Chatbot.Declined category.
        store.Append(new EventLogEntry { Category = "Safety.Intercept",  Message = "SAFETY-AFTER-CAP" });
        store.Append(new EventLogEntry { Category = "Chatbot.Declined",  Message = "DECLINE-AFTER-CAP" });

        string raw = ReadAllDayFiles("event-log");

        // Sentinel present exactly once.
        Assert.Equal(1, CountOccurrences(raw, "[truncated: daily cap reached]"));
        // Ordinary post-cap entries dropped.
        Assert.DoesNotContain("DROP-ME-1", raw);
        Assert.DoesNotContain("DROP-ME-2", raw);
        // Exempt post-cap entries landed.
        Assert.Contains("SAFETY-AFTER-CAP", raw);
        Assert.Contains("DECLINE-AFTER-CAP", raw);
    }

    /// <summary>A non-exempt Chatbot category that is NOT exactly Chatbot.Declined
    /// (e.g. Chatbot.SlashCommand) is still subject to the cap — the exemption is the
    /// exact category, not the whole Chatbot. prefix.</summary>
    [Fact]
    public void EventLog_PastDailyCap_NonDeclinedChatbotCategory_Drops()
    {
        var store = new FileEventLogStore(_dir);
        string padding = new string('x', 512);
        for (int i = 0; i < 3000; i++)
            store.Append(new EventLogEntry { Category = "Habituation.Profile", Message = padding });

        store.Append(new EventLogEntry { Category = "Chatbot.SlashCommand", Message = "SLASH-AFTER-CAP" });

        string raw = ReadAllDayFiles("event-log");
        Assert.DoesNotContain("SLASH-AFTER-CAP", raw);
    }

    private long DayFileBytes(string prefix)
    {
        var files = Directory.GetFiles(_dir, prefix + "-*.jsonl");
        return files.Sum(f => new FileInfo(f).Length);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private string ReadAllDayFiles(string prefix)
    {
        var files = Directory.GetFiles(_dir, prefix + "-*.jsonl");
        return string.Concat(files.Select(File.ReadAllText));
    }
}
