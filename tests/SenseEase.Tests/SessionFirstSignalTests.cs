using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SenseEase.App.Services;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Learning-loop Part D / Idea #2 (2026-06-21): the session-onset first-discomfort-signal capture. Covers
/// the pure <see cref="SessionFirstSignalBuilder"/> mean math (overall + per-channel, duration-only) and
/// the App <see cref="SessionOnsetLog"/> round-trip (append → read back, out-of-range dropped, malformed
/// lines skipped). The timing is REPORT-ONLY — nothing here touches the FMS partition or the relax gate.
/// </summary>
public sealed class SessionFirstSignalTests
{
    private static SessionOnsetRecord Rec(string channel, double offsetSec)
        => new() { Channel = channel, OffsetSeconds = offsetSec };

    // ── Pure aggregator ──────────────────────────────────────────────────────

    [Fact]
    public void Empty_ZeroSessions_NullMean()
    {
        var s = SessionFirstSignalBuilder.Build(Array.Empty<SessionOnsetRecord>());
        Assert.Equal(0, s.SessionCount);
        Assert.Null(s.MeanMinutesOverall);
        Assert.Empty(s.ByChannel);
    }

    [Fact]
    public void OddCount_MeanInMinutes()
    {
        // offsets 60s, 120s, 600s → mean 260s = 4.33 min.
        var s = SessionFirstSignalBuilder.Build(new[]
        {
            Rec(SessionOnsetChannels.Fms, 600),
            Rec(SessionOnsetChannels.Fms, 60),
            Rec(SessionOnsetChannels.Fms, 120),
        });
        Assert.Equal(3, s.SessionCount);
        Assert.Equal(4.33, s.MeanMinutesOverall!.Value, 2);
    }

    [Fact]
    public void EvenCount_MeanInMinutes()
    {
        // offsets 60,120,180,600 → mean 960/4 = 240s = 4.0 min.
        var s = SessionFirstSignalBuilder.Build(new[]
        {
            Rec(SessionOnsetChannels.Chat, 180),
            Rec(SessionOnsetChannels.Chat, 60),
            Rec(SessionOnsetChannels.Chat, 600),
            Rec(SessionOnsetChannels.Chat, 120),
        });
        Assert.Equal(4.0, s.MeanMinutesOverall!.Value, 2);
    }

    [Fact]
    public void PerChannel_GroupedWithOwnMeans()
    {
        var s = SessionFirstSignalBuilder.Build(new[]
        {
            Rec(SessionOnsetChannels.Consent, 300),   // consent: 300s = 5 min (single)
            Rec(SessionOnsetChannels.Fms, 60),
            Rec(SessionOnsetChannels.Fms, 180),        // fms: mean of 60,180 = 120s = 2 min
        });

        Assert.Equal(3, s.SessionCount);
        var consent = s.ByChannel.Single(c => c.Channel == SessionOnsetChannels.Consent);
        var fms     = s.ByChannel.Single(c => c.Channel == SessionOnsetChannels.Fms);
        Assert.Equal(1, consent.SessionCount);
        Assert.Equal(5.0, consent.MeanMinutes!.Value, 2);
        Assert.Equal(2, fms.SessionCount);
        Assert.Equal(2.0, fms.MeanMinutes!.Value, 2);
    }

    [Fact]
    public void InvalidOffsets_AreDropped()
    {
        // Negative + non-finite offsets and blank channels are excluded before aggregation.
        var s = SessionFirstSignalBuilder.Build(new[]
        {
            Rec(SessionOnsetChannels.Fms, -5),
            Rec(SessionOnsetChannels.Fms, double.NaN),
            Rec("", 100),
            Rec(SessionOnsetChannels.Fms, 120),   // the only valid one
        });
        Assert.Equal(1, s.SessionCount);
        Assert.Equal(2.0, s.MeanMinutesOverall!.Value, 2);
    }

    // ── App SessionOnsetLog round-trip ───────────────────────────────────────

    [Fact]
    public void Log_AppendThenReadAll_RoundTripsChannelAndOffset()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sense_onset_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SessionOnsetLog(dir, () => DateTime.UtcNow);
            log.Record(SessionOnsetChannels.Consent, 42.4, tier: 6, protectionState: "on");
            log.Record(SessionOnsetChannels.Chat, 130.0, tier: 4, protectionState: "off");

            var records = log.ReadAll();
            Assert.Equal(2, records.Count);
            Assert.Equal(SessionOnsetChannels.Consent, records[0].Channel);
            Assert.Equal(42.4, records[0].OffsetSeconds, 1);
            Assert.Equal(SessionOnsetChannels.Chat, records[1].Channel);

            // The aggregator reads the same rows: mean of 42.4 & 130 = 86.2s ≈ 1.44 min.
            var summary = SessionFirstSignalBuilder.Build(records);
            Assert.Equal(2, summary.SessionCount);
            Assert.Equal(Math.Round(((42.4 + 130.0) / 2.0) / 60.0, 2), summary.MeanMinutesOverall!.Value, 2);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Log_DropsOutOfRangeOffsets_AndSkipsMalformedLines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sense_onset_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SessionOnsetLog(dir, () => DateTime.UtcNow);
            log.Record(SessionOnsetChannels.Fms, -1, tier: 1, protectionState: "off");        // negative → not written
            log.Record(SessionOnsetChannels.Fms, double.NaN, tier: 1, protectionState: "off");// NaN → not written
            log.Record(SessionOnsetChannels.Fms, 90, tier: 5, protectionState: "on");          // valid

            // Inject a malformed line — ReadAll must skip it, keep the valid row.
            File.AppendAllText(Path.Combine(dir, "session_onset_log.jsonl"), "{ not json" + Environment.NewLine);

            var records = log.ReadAll();
            Assert.Single(records);
            Assert.Equal(90, records[0].OffsetSeconds, 1);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
