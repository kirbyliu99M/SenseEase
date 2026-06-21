using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Reporting;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Health-report redesign §8 (P3): the two-wall narrator <see cref="ClinicalReportNarrator"/>.
/// Pins failure isolation + the deterministic-fallback convergence: a null/cold model, a banned claim, or a
/// chat-artifact reply all degrade that section to its template (the report always renders full), while
/// clean prose is passed through; and every template itself passes the medical-overreach guard in both
/// languages (the golden path).</summary>
public class ClinicalReportNarratorTests
{
    // A stub that returns the same canned completion for every section (or null / not-loaded).
    private sealed class StubBot : IChatbotService
    {
        private readonly string? _reply;
        private readonly bool _loaded;
        public StubBot(string? reply, bool loaded = true) { _reply = reply; _loaded = loaded; }
        public bool IsModelAvailable => true;
        public bool IsModelLoaded => _loaded;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public void ClearHistory() { }
        public void Dispose() { }
        public Task<string?> CompleteShortAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
            => Task.FromResult(_reply);
    }

    private static ClinicalExport Sample() => new()
    {
        GeneratedUtc = "2026-06-21T08:00:00.0000000Z",
        Window   = new ExportWindow { FirstDateUtc = "2026-06-01", LastDateUtc = "2026-06-14", DaysObserved = 14 },
        Measured = new FmsAggregate { Count = 10, Mean = 6.2, BaselineMean = 8, OutcomeMean = 5 },
        OnVsOff  = new OnOffContrast { CountOn = 6, CountOff = 4, MeanFmsOn = 5, MeanFmsOff = 9, Delta = 4 },
        DailySeries = new List<DailySeriesPoint>
        {
            new() { Date = "2026-06-01", MeasuredFmsMean = 8, OfiP90 = 70, MaskActivations = 3 },
            new() { Date = "2026-06-03", MeasuredFmsMean = 5, OfiP90 = 90, MaskActivations = 5 },
        },
    };

    private static ReportPosture Posture() => new()
    { Strength = ProtectionStrength.Balanced, HabituationLevel = 42, OffsetPp = 3.5, OffsetCapPp = 10, CurrentTier = 4 };

    private static void AssertEverySectionCleanAndNonEmpty(ReportNarrative n)
    {
        foreach (string s in new[] { n.Summary, n.ComfortTrend, n.Protection, n.Exposure })
        {
            Assert.False(string.IsNullOrWhiteSpace(s));
            Assert.False(ReportClaimGuard.ContainsBannedClaim(s), $"section leaked a banned claim: {s}");
        }
    }

    [Fact]
    public async Task NullChatbot_UsesDeterministicNarrative_WhichPassesTheGuard()
    {
        var n = await new ClinicalReportNarrator(null).BuildAsync(Sample(), Posture(), english: true);
        AssertEverySectionCleanAndNonEmpty(n);
    }

    [Fact]
    public async Task ModelNotLoaded_FallsBack_NoCall()
    {
        var n = await new ClinicalReportNarrator(new StubBot("anything", loaded: false))
            .BuildAsync(Sample(), Posture(), english: true);
        AssertEverySectionCleanAndNonEmpty(n);
        Assert.DoesNotContain("anything", n.Summary);   // the model was never consulted
    }

    [Fact]
    public async Task BannedClaim_IsRejected_SectionFallsBackToTemplate()
    {
        var n = await new ClinicalReportNarrator(new StubBot("Your tolerance has clearly improved this month."))
            .BuildAsync(Sample(), Posture(), english: true);

        Assert.False(ReportClaimGuard.ContainsBannedClaim(n.Summary));
        Assert.DoesNotContain("tolerance", n.Summary, StringComparison.OrdinalIgnoreCase);
        AssertEverySectionCleanAndNonEmpty(n);
    }

    [Fact]
    public async Task ChatArtifactReply_IsRejected()
    {
        // The chat scrubber's English HonestNoActionLine, were it to leak through CompleteShortAsync.
        var n = await new ClinicalReportNarrator(
                new StubBot("I haven't changed anything just now. Use the +/- control on the bar."))
            .BuildAsync(Sample(), Posture(), english: true);

        Assert.DoesNotContain("control on the bar", n.Summary);
        AssertEverySectionCleanAndNonEmpty(n);
    }

    [Fact]
    public async Task WrongLanguageReply_IsRejected()
    {
        // EN report, but the model returned Chinese → reject (script contradicts the pin).
        var n = await new ClinicalReportNarrator(new StubBot("這是一段中文回覆。"))
            .BuildAsync(Sample(), Posture(), english: true);
        Assert.DoesNotContain("中文", n.Summary);
        AssertEverySectionCleanAndNonEmpty(n);
    }

    [Fact]
    public async Task CleanProse_IsUsedVerbatim()
    {
        const string clean = "Here are the figures from the period, stated plainly.";
        var n = await new ClinicalReportNarrator(new StubBot(clean))
            .BuildAsync(Sample(), Posture(), english: true);
        Assert.Equal(clean, n.Summary);
    }

    [Fact]
    public async Task OnlySummaryIsModelPhrased_OtherSectionsAreDeterministic()
    {
        // Option (b): one clean model reply is accepted ONLY for the Summary; the other three sections never
        // consult the model, so the marker can't appear in them.
        const string marker = "These are the plainly stated figures.";   // no digits, no banned claim
        var n = await new ClinicalReportNarrator(new StubBot(marker)).BuildAsync(Sample(), Posture(), english: true);
        Assert.Equal(marker, n.Summary);
        Assert.DoesNotContain(marker, n.ComfortTrend);
        Assert.DoesNotContain(marker, n.Protection);
        Assert.DoesNotContain(marker, n.Exposure);
        AssertEverySectionCleanAndNonEmpty(n);
    }

    [Fact]
    public async Task SummaryWithAFabricatedNumber_FallsBackToTemplate()
    {
        // Sample(): DaysObserved=14, Measured.Count=10, Estimated.Count=0 → allowed {14,10,0}. 999 was never
        // handed to the model, so the number-lock rejects the section to its deterministic template.
        var n = await new ClinicalReportNarrator(new StubBot("This report covers 999 days of careful use."))
            .BuildAsync(Sample(), Posture(), english: true);
        Assert.DoesNotContain("999", n.Summary);
        AssertEverySectionCleanAndNonEmpty(n);
    }

    [Fact]
    public async Task SummaryUsingOnlySuppliedNumbers_IsKept()
    {
        const string ok = "Over 14 days you logged 10 comfort check-ins.";   // both digits are in the allowed set
        var n = await new ClinicalReportNarrator(new StubBot(ok)).BuildAsync(Sample(), Posture(), english: true);
        Assert.Equal(ok, n.Summary);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeterministicTemplates_PassGuard_InBothLanguages(bool english)
    {
        var n = await new ClinicalReportNarrator(null).BuildAsync(Sample(), Posture(), english);
        AssertEverySectionCleanAndNonEmpty(n);
    }
}
