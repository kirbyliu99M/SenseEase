using System;
using System.IO;
using System.Linq;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 Phase-1 (option C): every symptom/comfort data-point lands in BOTH the structured
/// system-of-record (symptom_log.jsonl) AND the main event log as a durable, human-readable
/// Symptom.* mirror — so the data is analyzable and visible, without conflating measured (PRO)
/// with estimated values.
/// </summary>
public sealed class SymptomEventRecorderTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "senseease_recorder_test_" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
    }

    [Fact]
    public void Record_MeasuredReport_WritesStructured_AndMirrorsSymptomReport()
    {
        var store = new FileSymptomEventStore(_path);
        var log = new InMemoryEventLogStore();
        var rec = new SymptomEventRecorder(store, log);

        rec.Record(new SymptomEvent
        {
            Kind = SymptomEvent.Kinds.FmsReport, Measured = true,
            Scale = SymptomEvent.Scales.Fms20, Value = 8, Source = SymptomEvent.Sources.Stars,
            ContextTier = 6, InterventionState = SymptomEvent.InterventionStates.On,
            Trigger = SymptomEvent.Triggers.SessionEnd,
        });

        // Structured system-of-record.
        var stored = store.ReadAll();
        Assert.Single(stored);
        Assert.True(stored[0].Measured);
        Assert.Equal(8, stored[0].Value);

        // Mirror into the main event log under the durable Symptom.Report category.
        var mirror = log.Recent().Single();
        Assert.Equal("Symptom.Report", mirror.Category);
        Assert.Contains("8/20", mirror.Message);
        Assert.Contains("measured", mirror.Message);
        Assert.Contains("tier 6", mirror.Message);
    }

    [Fact]
    public void Record_Estimate_MirrorsSymptomEstimate_NotConflatedWithMeasured()
    {
        var store = new FileSymptomEventStore(_path);
        var log = new InMemoryEventLogStore();
        var rec = new SymptomEventRecorder(store, log);

        rec.Record(new SymptomEvent
        {
            Kind = SymptomEvent.Kinds.FmsEstimate, Measured = false,
            Scale = SymptomEvent.Scales.Fms20, Value = 5, Source = SymptomEvent.Sources.NlDaily,
            ContextTier = 7, InterventionState = SymptomEvent.InterventionStates.On,
        });

        var mirror = log.Recent().Single();
        Assert.Equal("Symptom.Estimate", mirror.Category);   // distinct category from a PRO
        Assert.Contains("estimated", mirror.Message);
        Assert.Contains("~5/20", mirror.Message);            // ~ marks it as not-measured
        Assert.False(store.ReadAll()[0].Measured);
    }

    [Fact]
    public void BuildMirror_KeepsCategoryDurable_SoItPersistsInForensicLog()
    {
        // The Symptom. prefix must be a durable category (persists to event-log-*.jsonl + the
        // dashboard). Assert the mirror category carries that prefix.
        var report = SymptomEventRecorder.BuildMirror(new SymptomEvent { Measured = true, Value = 2, ContextTier = 3 });
        var estimate = SymptomEventRecorder.BuildMirror(new SymptomEvent { Measured = false, Value = 9, ContextTier = 9 });
        Assert.StartsWith("Symptom.", report.Category);
        Assert.StartsWith("Symptom.", estimate.Category);
    }
}
