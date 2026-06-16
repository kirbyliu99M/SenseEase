using System;
using System.IO;
using System.Linq;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// B3.0 (per-app learning — foundation): the engine stamps the coarse, consent-gated
/// foreground key onto the durable events it emits, and that key survives the JSONL
/// round-trip. This is the additive, INERT plumbing layer — nothing consumes
/// <see cref="EventLogEntry.ForegroundKey"/> yet, so these tests pin the contract the
/// later per-app bucketing (B3.1) will read against, and prove the global-bucket
/// (null) default keeps today's app-agnostic behaviour intact.
/// </summary>
public class EventLogForegroundKeyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"senseease-fgkey-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Engine stamping ────────────────────────────────────────────────────────

    [Fact]
    public void Engine_StampsCurrentForegroundKey_OnDurableSpikeEvent()
    {
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        engine.NotifyForegroundAppCategory("video/vlc");
        engine.ApplyUserFeedback(-50);   // → Habituation.Spike (a durable event)

        var spike = eventLog.Recent().First(e => e.Category == "Habituation.Spike");
        Assert.Equal("video/vlc", spike.ForegroundKey);
    }

    [Fact]
    public void Engine_DefaultsToNullForegroundKey_GlobalBucket()
    {
        // No NotifyForegroundAppCategory call (the common case: consent off / unknown app).
        // Every emitted event must carry a null key so per-app bucketing falls back to global.
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        engine.ApplyUserFeedback(-50);

        var spike = eventLog.Recent().First(e => e.Category == "Habituation.Spike");
        Assert.Null(spike.ForegroundKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Engine_BlankForegroundKey_NormalisesToNull(string? key)
    {
        // A blank/whitespace push (e.g. consent toggled off → filter returns "") must
        // collapse to the global bucket, never an empty-string bucket of its own.
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        engine.NotifyForegroundAppCategory("video/vlc"); // first set a real key…
        engine.NotifyForegroundAppCategory(key);          // …then blank it
        engine.ApplyUserFeedback(-50);

        var spike = eventLog.Recent().First(e => e.Category == "Habituation.Spike");
        Assert.Null(spike.ForegroundKey);
    }

    [Fact]
    public void Engine_ForegroundKeyTracksLatestPush_AcrossEvents()
    {
        // An alt-tab between two spikes buckets each to the app that was foreground at emit.
        var eventLog = new InMemoryEventLogStore();
        var engine = new MockInferenceEngine(null, null, null, eventLog);

        engine.NotifyForegroundAppCategory("game/xyz");
        engine.ApplyUserFeedback(-50);
        engine.NotifyForegroundAppCategory("office/word");
        engine.ApplyUserFeedback(-50);

        var spikes = eventLog.Recent().Where(e => e.Category == "Habituation.Spike").ToList();
        Assert.Contains(spikes, e => e.ForegroundKey == "game/xyz");
        Assert.Contains(spikes, e => e.ForegroundKey == "office/word");
    }

    // ── Durable JSONL round-trip ───────────────────────────────────────────────

    [Fact]
    public void FileStore_RoundTripsForegroundKey()
    {
        var now = DateTime.UtcNow;
        var store = new FileEventLogStore(_tempDir);
        store.Append(new EventLogEntry
        {
            Timestamp = now,
            Category = "Habituation.Spike",   // a durable-prefixed category (persisted to disk)
            ForegroundKey = "video/vlc",
            Pressure = 90,
        });

        var read = new FileEventLogStore(_tempDir).ReadDurable(now.AddMinutes(-5));
        var entry = Assert.Single(read.Where(e => e.Category == "Habituation.Spike"));
        Assert.Equal("video/vlc", entry.ForegroundKey);
    }

    [Fact]
    public void FileStore_NullForegroundKey_RoundTripsAsNull()
    {
        var now = DateTime.UtcNow;
        var store = new FileEventLogStore(_tempDir);
        store.Append(new EventLogEntry
        {
            Timestamp = now,
            Category = "Habituation.Spike",
            ForegroundKey = null,
            Pressure = 90,
        });

        var read = new FileEventLogStore(_tempDir).ReadDurable(now.AddMinutes(-5));
        var entry = Assert.Single(read.Where(e => e.Category == "Habituation.Spike"));
        Assert.Null(entry.ForegroundKey);
    }

    [Fact]
    public void FileStore_LegacyLineWithoutForegroundKey_DeserialisesToNull()
    {
        // Backward-compat: a 7-day rolling file written before B3 has no ForegroundKey field.
        // Those lines must read back with a null key (global bucket), never throw.
        Directory.CreateDirectory(_tempDir);
        string dayFile = Path.Combine(_tempDir, $"event-log-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        string iso = DateTime.UtcNow.ToString("o");
        File.WriteAllText(dayFile,
            "{\"Timestamp\":\"" + iso + "\",\"Category\":\"Habituation.Spike\",\"Pressure\":90}\n");

        var read = new FileEventLogStore(_tempDir).ReadDurable(DateTime.UtcNow.AddMinutes(-5));
        var entry = Assert.Single(read.Where(e => e.Category == "Habituation.Spike"));
        Assert.Null(entry.ForegroundKey);
    }
}
