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

    private string ReadAllDayFiles(string prefix)
    {
        var files = Directory.GetFiles(_dir, prefix + "-*.jsonl");
        return string.Concat(files.Select(File.ReadAllText));
    }
}
