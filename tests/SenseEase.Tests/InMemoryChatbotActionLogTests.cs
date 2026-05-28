using System;
using System.Threading;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 (Phase 7g): contract tests for InMemoryChatbotActionLog. Focuses on
/// MostRecentDeclineFor (used by Processor rule 5) and the Recent
/// chronological ordering invariant.
/// </summary>
public class InMemoryChatbotActionLogTests
{
    private static ProfileOverrideProposal MakeProposal(OverrideIntent intent)
        => new ProfileOverrideProposal(
            Intent: intent,
            TargetField: "x",
            ProposedValue: 1.0,
            Rationale: "t",
            ExpiresAtUtc: DateTime.UtcNow.AddDays(2));

    [Fact]
    public void MostRecentDeclineFor_NoEntries_ReturnsNull()
    {
        var log = new InMemoryChatbotActionLog();
        Assert.Null(log.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity));
    }

    [Fact]
    public void MostRecentDeclineFor_OnlyAccepts_ReturnsNull()
    {
        var log = new InMemoryChatbotActionLog();
        log.Record(ChatbotActionKind.Accepted, MakeProposal(OverrideIntent.AdjustSensitivity));
        Assert.Null(log.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity));
    }

    [Fact]
    public void MostRecentDeclineFor_DeclineRecorded_ReturnsTimestamp()
    {
        var log = new InMemoryChatbotActionLog();
        var before = DateTime.UtcNow;
        log.Record(ChatbotActionKind.Declined, MakeProposal(OverrideIntent.AdjustSensitivity));
        var after = DateTime.UtcNow;

        var when = log.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity);
        Assert.NotNull(when);
        Assert.InRange(when!.Value, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public void MostRecentDeclineFor_DifferentIntent_ReturnsNull()
    {
        var log = new InMemoryChatbotActionLog();
        log.Record(ChatbotActionKind.Declined, MakeProposal(OverrideIntent.SwitchProfile));
        Assert.Null(log.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity));
    }

    [Fact]
    public void MostRecentDeclineFor_MultipleDeclines_ReturnsMostRecent()
    {
        var log = new InMemoryChatbotActionLog();
        log.Record(ChatbotActionKind.Declined, MakeProposal(OverrideIntent.AdjustSensitivity));
        Thread.Sleep(20);
        var midpoint = DateTime.UtcNow;
        Thread.Sleep(20);
        log.Record(ChatbotActionKind.Declined, MakeProposal(OverrideIntent.AdjustSensitivity));

        var when = log.MostRecentDeclineFor(OverrideIntent.AdjustSensitivity);
        Assert.NotNull(when);
        Assert.True(when!.Value > midpoint);
    }

    [Fact]
    public void Recent_ReturnsNewestFirst()
    {
        var log = new InMemoryChatbotActionLog();
        log.Record(ChatbotActionKind.Proposed,  MakeProposal(OverrideIntent.AdjustSensitivity));
        log.Record(ChatbotActionKind.Accepted,  MakeProposal(OverrideIntent.AdjustSensitivity));
        log.Record(ChatbotActionKind.Declined,  MakeProposal(OverrideIntent.SwitchProfile));

        var recent = log.Recent(10);
        Assert.Equal(3, recent.Count);
        Assert.Equal(ChatbotActionKind.Declined, recent[0].Kind);
        Assert.Equal(ChatbotActionKind.Accepted, recent[1].Kind);
        Assert.Equal(ChatbotActionKind.Proposed, recent[2].Kind);
    }

    [Fact]
    public void Recent_RespectsCountLimit()
    {
        var log = new InMemoryChatbotActionLog();
        for (int i = 0; i < 5; i++)
            log.Record(ChatbotActionKind.Proposed, MakeProposal(OverrideIntent.AdjustSensitivity));

        Assert.Equal(3, log.Recent(3).Count);
        Assert.Equal(5, log.Recent(10).Count);
        Assert.Empty(log.Recent(0));
    }
}
