using System;
using System.Globalization;
using System.IO;
using System.Linq;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 Phase-1: the shared EventLog foundation (symptom_log.jsonl). Pins the round-trip,
/// the measured-vs-estimated separation (D-17/18/19/21), timestamp stamping, recency, and the
/// retention trim — the storage the 5★/FMS rating (D-18/19) and daily NL→FMS estimate (D-21)
/// write into, and the ON-vs-OFF delta / Theme-2 join read from.
/// </summary>
public sealed class SymptomEventStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "senseease_symptom_test_" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
    }

    private SymptomEvent Report(int value, int tier, string state) => new()
    {
        Kind = SymptomEvent.Kinds.FmsReport,
        Measured = true,
        Scale = SymptomEvent.Scales.Fms20,
        Value = value,
        Source = SymptomEvent.Sources.Stars,
        ContextTier = tier,
        InterventionState = state,
        Trigger = SymptomEvent.Triggers.SessionEnd,
        ForegroundKey = "category:video",
    };

    [Fact]
    public void Append_ThenReadAll_RoundTripsAllFields()
    {
        var store = new FileSymptomEventStore(_path);
        store.Append(Report(8, 6, SymptomEvent.InterventionStates.On));

        var all = store.ReadAll();
        Assert.Single(all);
        var e = all[0];
        Assert.Equal(SymptomEvent.Kinds.FmsReport, e.Kind);
        Assert.True(e.Measured);
        Assert.Equal("fms20", e.Scale);
        Assert.Equal(8, e.Value);
        Assert.Equal("stars", e.Source);
        Assert.Equal(6, e.ContextTier);
        Assert.Equal("on", e.InterventionState);
        Assert.Equal("category:video", e.ForegroundKey);
        Assert.False(e.Exportable);        // sprint-locked: never exported raw
        Assert.True(e.DeletableOnClear);   // H24 covers it
        Assert.False(string.IsNullOrWhiteSpace(e.TsUtc)); // stamped on append
    }

    [Fact]
    public void Append_PersistsSnakeCaseJsonl_OneObjectPerLine()
    {
        var store = new FileSymptomEventStore(_path);
        store.Append(Report(3, 5, SymptomEvent.InterventionStates.Off));
        store.Append(Report(12, 8, SymptomEvent.InterventionStates.On));

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"context_tier\"", lines[0]);     // snake_case
        Assert.Contains("\"intervention_state\"", lines[0]);
        Assert.DoesNotContain("ContextTier", lines[0]);    // not PascalCase
    }

    [Fact]
    public void Measured_And_Estimated_AreStoredDistinctly()
    {
        var store = new FileSymptomEventStore(_path);
        store.Append(Report(10, 7, SymptomEvent.InterventionStates.On)); // PRO (measured)
        store.Append(new SymptomEvent
        {
            Kind = SymptomEvent.Kinds.FmsEstimate,
            Measured = false,
            Scale = SymptomEvent.Scales.Fms20,
            Value = 6,
            Source = SymptomEvent.Sources.NlDaily,
            ContextTier = 7,
            InterventionState = SymptomEvent.InterventionStates.On,
        });

        var all = store.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Single(all.Where(e => e.Measured && e.Kind == SymptomEvent.Kinds.FmsReport));
        Assert.Single(all.Where(e => !e.Measured && e.Kind == SymptomEvent.Kinds.FmsEstimate
                                     && e.Source == SymptomEvent.Sources.NlDaily));
    }

    [Fact]
    public void ReadRecent_ReturnsLastN_OldestFirst()
    {
        var store = new FileSymptomEventStore(_path);
        for (int v = 0; v < 5; v++) store.Append(Report(v, 6, SymptomEvent.InterventionStates.On));

        var recent = store.ReadRecent(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(3, recent[0].Value);
        Assert.Equal(4, recent[1].Value);
    }

    [Fact]
    public void TrimOlderThan_DropsOldKeepsRecent_AndKeepsUnparseableTimestamps()
    {
        var store = new FileSymptomEventStore(_path);

        var old = Report(1, 6, SymptomEvent.InterventionStates.On);
        old.TsUtc = DateTime.UtcNow.AddDays(-40).ToString("o", CultureInfo.InvariantCulture);
        store.Append(old);

        var fresh = Report(2, 6, SymptomEvent.InterventionStates.On); // stamped now
        store.Append(fresh);

        var weird = Report(3, 6, SymptomEvent.InterventionStates.On);
        weird.TsUtc = "not-a-timestamp";  // must be KEPT, never dropped on a parse hiccup
        store.Append(weird);

        store.TrimOlderThan(30);

        var all = store.ReadAll();
        Assert.DoesNotContain(all, e => e.Value == 1);  // 40 days old → trimmed
        Assert.Contains(all, e => e.Value == 2);        // fresh → kept
        Assert.Contains(all, e => e.Value == 3);        // unparseable ts → kept
    }
}
