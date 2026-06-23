using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Reporting;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Health-report redesign §8 (P4) — the composition + I/O seam <see cref="ClinicalReportService"/>
/// that the pure collaborators do not cover (#10). Pins: WriteAsync writes BOTH the JSON data record and the
/// HTML export from ONE composed dataset (Kirby: "JSON is the data recording, HTML is the export"); onset +
/// consent + satisfaction are composed in; the posture allowlist maps the raw profile enum away and degrades
/// to null when the profile store throws; the window filter scopes the data; and writes are atomic/replace.</summary>
public class ClinicalReportServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _symPath;
    private readonly string _behPath;

    public ClinicalReportServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "se-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _symPath = Path.Combine(_dir, "symptom_log.jsonl");
        _behPath = Path.Combine(_dir, "behavior_log.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class StubProfileStore : IHabituationProfileStore
    {
        private readonly HabituationProfileSnapshot? _snap;
        public StubProfileStore(HabituationProfileSnapshot? snap) => _snap = snap;
        public HabituationProfileSnapshot? Load() => _snap;
        public void Save(HabituationProfileSnapshot snapshot) { }
    }

    private sealed class ThrowingProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Load() => throw new IOException("boom");
        public void Save(HabituationProfileSnapshot snapshot) { }
    }

    private FileSymptomEventStore SeededSymptoms()
    {
        var store = new FileSymptomEventStore(_symPath);
        store.Append(ComfortRatingScale.BuildMeasuredFmsReport(8, 4, maskActive: true,  foregroundKey: null, trigger: "spike"));
        store.Append(ComfortRatingScale.BuildMeasuredFmsReport(4, 4, maskActive: false, foregroundKey: null, trigger: "spike"));
        return store;
    }

    private static Func<IReadOnlyList<SessionOnsetRecord>> Onset() => () => new List<SessionOnsetRecord>
    {
        new() { Channel = SessionOnsetChannels.Consent, OffsetSeconds = 300 },
        new() { Channel = SessionOnsetChannels.Fms,     OffsetSeconds = 600 },
    };

    private ClinicalReportService Service(IHabituationProfileStore? profile, bool withOnset = true, bool withSat = true)
        => new(
            SeededSymptoms(),
            new DailyBehaviorAggregateWriter(_behPath),
            profile,
            chatbot: null,
            onsetReader: withOnset ? Onset() : null,
            satisfactionReader: withSat ? () => new SatisfactionSummary { MeanScore = 4.0, ScaleMax = 5, ResponseCount = 3 } : null);

    [Fact]
    public async Task WriteAsync_WritesBothArtifacts_FromOneComposedDataset()
    {
        string html = Path.Combine(_dir, "r.html");
        string json = Path.Combine(_dir, "r.json");
        await Service(new StubProfileStore(new HabituationProfileSnapshot
        {
            Profile = "RESILIENT", HabituationLevel = 50, HabituationOffsetPp = 2, HabituationOffsetCapPp = 10, CurrentVimsTier = 3,
        })).WriteAsync(html, json, english: true, nowUtc: DateTime.UtcNow);

        Assert.True(File.Exists(html));
        Assert.True(File.Exists(json));

        string h = File.ReadAllText(html);
        Assert.StartsWith("<!DOCTYPE html>", h);
        Assert.Contains("Time to first discomfort signal", h);   // onset composed + rendered
        Assert.Contains("Satisfaction", h);                      // CSAT tile composed + rendered
        Assert.Contains("light", h);                             // RESILIENT → Light strength word (enum mapped away)

        // The JSON data record carries the FULL composed dataset (not the old partial build).
        string j = File.ReadAllText(json);
        Assert.Contains("\"daily_series\"", j);
        Assert.Contains("\"onset_timing\"", j);
        Assert.Contains("\"consent_engagement\"", j);
        Assert.Contains("\"satisfaction\"", j);
        Assert.Contains("\"de_identified\": true", j);
    }

    [Fact]
    public async Task WriteAsync_ProfileStoreThrows_OmitsPosture_StillWritesReport()
    {
        string html = Path.Combine(_dir, "r.html");
        string json = Path.Combine(_dir, "r.json");
        await Service(new ThrowingProfileStore()).WriteAsync(html, json, english: true, nowUtc: DateTime.UtcNow);

        string h = File.ReadAllText(html);
        Assert.StartsWith("<!DOCTYPE html>", h);
        Assert.DoesNotContain("Protection strength", h);   // ReadPosture caught the throw → posture omitted
    }

    [Fact]
    public async Task WriteAsync_SecondCall_AtomicallyReplaces()
    {
        string html = Path.Combine(_dir, "r.html");
        string json = Path.Combine(_dir, "r.json");
        var svc = Service(null);
        await svc.WriteAsync(html, json, english: true, nowUtc: DateTime.UtcNow);
        await svc.WriteAsync(html, json, english: false, nowUtc: DateTime.UtcNow);   // overwrite, switch language

        string h = File.ReadAllText(html);
        Assert.Contains("視覺舒適報告", h);                 // the second (ZH) write replaced the first
        Assert.DoesNotContain("Visual comfort report", h);
        Assert.False(File.Exists(html + ".tmp"));           // no temp left behind
    }

    [Fact]
    public async Task WriteAsync_FutureWindow_ScopesOutAllEvents()
    {
        string html = Path.Combine(_dir, "r.html");
        string json = Path.Combine(_dir, "r.json");
        await Service(null).WriteAsync(html, json, english: true, nowUtc: DateTime.UtcNow,
            sinceUtc: DateTime.UtcNow.AddDays(1));   // window starts tomorrow → today's seeded events excluded

        string h = File.ReadAllText(html);
        Assert.Contains("Not enough data yet.", h);   // the comfort chart has no in-window points
    }
}
