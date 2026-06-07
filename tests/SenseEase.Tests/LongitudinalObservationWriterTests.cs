using System;
using System.Collections.Generic;
using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 (Phase 7g §4.3.8) + UM2: contract tests for LongitudinalObservationWriter.
/// UM2 moved observations from a flat 50-entry FIFO to monthly buckets
/// (LongitudinalObservationsV2, keyed "YYYY-MM"): 30 entries/bucket, same-month
/// dedup, 12-month retention. These tests assert against the current month's bucket.
/// </summary>
public class LongitudinalObservationWriterTests
{
    private sealed class FakeProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Snapshot { get; set; }
            = new HabituationProfileSnapshot();
        public HabituationProfileSnapshot? Load() => Snapshot;
        public void Save(HabituationProfileSnapshot snapshot) => Snapshot = snapshot;
    }

    /// <summary>The entries in the current ("YYYY-MM") month's bucket — where the
    /// writer appends.</summary>
    private static List<ObservationEntry> CurrentBucket(FakeProfileStore store)
    {
        string month = DateTime.UtcNow.ToString("yyyy-MM");
        return store.Snapshot!.LongitudinalObservationsV2.TryGetValue(month, out var b)
            ? b : new List<ObservationEntry>();
    }

    [Fact]
    public void Append_NormalString_AppendsToCurrentMonthBucket()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        bool wrote = writer.Append("user reports symptoms worse on Mondays");

        Assert.True(wrote);
        var entry = Assert.Single(CurrentBucket(store));
        // UM2: body is stored clean (no date prefix); the timestamp is a separate ISO field.
        Assert.Contains("Mondays", entry.Text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", entry.Ts);
        // The deprecated flat list is no longer written.
        Assert.Empty(store.Snapshot!.LongitudinalObservations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\t\n")]
    public void Append_BlankInput_Skipped(string? input)
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        bool wrote = writer.Append(input);

        Assert.False(wrote);
        Assert.Empty(CurrentBucket(store));
    }

    [Fact]
    public void Append_NoSnapshot_Skipped()
    {
        var store = new FakeProfileStore { Snapshot = null };
        var writer = new LongitudinalObservationWriter(store);

        bool wrote = writer.Append("something");

        Assert.False(wrote);
    }

    [Fact]
    public void Append_LongString_TruncatedTo200Chars()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);
        string longText = new string('a', 250);

        writer.Append(longText);

        var entry = Assert.Single(CurrentBucket(store));
        // UM2: body capped at MaxObservationLength (no date prefix on the body now).
        Assert.True(entry.Text.Length <= LongitudinalObservationWriter.MaxObservationLength);
    }

    [Fact]
    public void Append_DuplicateOfRecent_SameMonth_Skipped()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        Assert.True(writer.Append("the user is feeling worse on weekends"));
        Assert.False(writer.Append("the user is feeling worse on weekends"));
        // Slightly different wording but same tokens → still dedup hit.
        Assert.False(writer.Append("the user is feeling worse on weekends now"));
        Assert.Single(CurrentBucket(store));
    }

    [Fact]
    public void Append_DifferentObservation_AppendsBoth()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        Assert.True(writer.Append("symptoms worse on weekends"));
        Assert.True(writer.Append("user prefers tighter mask in evenings"));
        Assert.Equal(2, CurrentBucket(store).Count);
    }

    [Fact]
    public void Append_BeyondBucketCap_OldestEvicted()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        // 60 unique observations — well over the 30-per-bucket cap.
        for (int i = 0; i < 60; i++)
            writer.Append($"observation number {i} with unique tokens xyzzy_{i}");

        var bucket = CurrentBucket(store);
        Assert.Equal(LongitudinalObservationWriter.MaxObservationsPerBucket, bucket.Count);
        // Cap 30 keeps the last 30 (i=30..59): newest survives, oldest evicted.
        Assert.Contains(bucket, e => e.Text.Contains("xyzzy_59"));
        Assert.Contains(bucket, e => e.Text.Contains("xyzzy_30"));
        Assert.DoesNotContain(bucket, e => e.Text.Contains("xyzzy_0"));   // i=0 evicted
        Assert.DoesNotContain(bucket, e => e.Text.Contains("xyzzy_29"));  // i=29 evicted
    }

    [Fact]
    public void JaccardSimilarity_IdenticalStrings_One()
    {
        double s = LongitudinalObservationWriter.JaccardSimilarity("a b c", "a b c");
        Assert.Equal(1.0, s);
    }

    [Fact]
    public void JaccardSimilarity_NoOverlap_Zero()
    {
        double s = LongitudinalObservationWriter.JaccardSimilarity("a b", "c d");
        Assert.Equal(0.0, s);
    }
}
