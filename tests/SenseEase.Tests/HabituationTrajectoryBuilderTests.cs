using System;
using System.Collections.Generic;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M2 (Phase 6c): contract tests for HabituationTrajectoryBuilder. The
/// builder produces the one-line summary that lands in the
/// <c>[habituation_state]</c> block's <c>trajectory</c> field. Phase 8
/// E2E scenarios will verify the LLM grounds replies in this summary;
/// these tests pin the summary structure independent of LLM behaviour.
/// </summary>
public class HabituationTrajectoryBuilderTests
{
    [Fact]
    public void Build_NullEventLog_ReturnsEmptyString()
    {
        string result = HabituationTrajectoryBuilder.BuildSummary(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Build_NoHabituationEvents_ReturnsNoActivityMessage()
    {
        var log = new InMemoryEventLogStore();
        // Pollute with non-habituation events that should be IGNORED.
        log.Append(new EventLogEntry { Category = "System",   Message = "boot" });
        log.Append(new EventLogEntry { Category = "Calibration", Message = "completed" });

        string result = HabituationTrajectoryBuilder.BuildSummary(log, windowDays: 30);
        Assert.Equal("no habituation activity in last 30 days", result);
    }

    [Fact]
    public void Build_OnlyLevelChanges_SummarisesLevelChangeCount()
    {
        var log = new InMemoryEventLogStore();
        var now = DateTime.UtcNow;
        log.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-5),  Message = "level rose 5.0 → 8.0" });
        log.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-3),  Message = "level rose 8.0 → 12.0" });
        log.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-1),  Message = "level rose 12.0 → 14.0" });

        string result = HabituationTrajectoryBuilder.BuildSummary(log, windowDays: 30, now: now);
        Assert.Contains("3 level changes", result);
        Assert.DoesNotContain("spike", result);
    }

    [Fact]
    public void Build_OnlySpikes_SummarisesSpikeCount()
    {
        var log = new InMemoryEventLogStore();
        var now = DateTime.UtcNow;
        log.Append(new EventLogEntry { Category = "Habituation.Spike", Timestamp = now.AddDays(-7), Message = "severe spike" });
        log.Append(new EventLogEntry { Category = "Habituation.Spike", Timestamp = now.AddDays(-2), Message = "severe spike" });

        string result = HabituationTrajectoryBuilder.BuildSummary(log, windowDays: 30, now: now);
        Assert.Contains("2 spikes", result);
        Assert.DoesNotContain("level changes", result);
    }

    [Fact]
    public void Build_LevelAndSpikeMix_SummarisesBoth()
    {
        var log = new InMemoryEventLogStore();
        var now = DateTime.UtcNow;
        log.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-10), Message = "level rose 5.0 → 8.0" });
        log.Append(new EventLogEntry { Category = "Habituation.Spike",        Timestamp = now.AddDays(-7),  Message = "severe spike" });
        log.Append(new EventLogEntry { Category = "Habituation.LevelChanged", Timestamp = now.AddDays(-3),  Message = "level rose 6.0 → 10.0" });

        string result = HabituationTrajectoryBuilder.BuildSummary(log, windowDays: 30, now: now);
        Assert.Contains("2 level changes", result);
        Assert.Contains("1 spike", result);
    }

    [Fact]
    public void Build_OldEventsOutsideWindow_AreExcluded()
    {
        var log = new InMemoryEventLogStore();
        var now = DateTime.UtcNow;
        // Inside window
        log.Append(new EventLogEntry { Category = "Habituation.Spike", Timestamp = now.AddDays(-5), Message = "spike" });
        // Outside window (older than 30 days)
        log.Append(new EventLogEntry { Category = "Habituation.Spike", Timestamp = now.AddDays(-60), Message = "old spike" });

        string result = HabituationTrajectoryBuilder.BuildSummary(log, windowDays: 30, now: now);
        Assert.Contains("1 spike", result);
        Assert.DoesNotContain("2", result);
    }

    [Fact]
    public void Build_ProfileChangeOnly_TerseSummary()
    {
        // Profile change events alone (no level/spike) get a generic count
        // since there's nothing quantitative to summarise.
        var log = new InMemoryEventLogStore();
        var now = DateTime.UtcNow;
        log.Append(new EventLogEntry { Category = "Habituation.Profile", Timestamp = now.AddDays(-2), Message = "RESILIENT → SENSITIVE" });

        string result = HabituationTrajectoryBuilder.BuildSummary(log, windowDays: 30, now: now);
        Assert.Contains("habituation events", result);
    }
}
