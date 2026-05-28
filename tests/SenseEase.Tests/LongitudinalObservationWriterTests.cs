using System.Linq;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M3 (Phase 7g §4.3.8): contract tests for LongitudinalObservationWriter.
/// Covers happy-path append, blank skip, length truncation, dedup, and
/// FIFO eviction at the 50-entry cap.
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

    [Fact]
    public void Append_NormalString_AppendsWithDatePrefix()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        bool wrote = writer.Append("user reports symptoms worse on Mondays");

        Assert.True(wrote);
        var obs = Assert.Single(store.Snapshot!.LongitudinalObservations);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}:", obs);
        Assert.Contains("Mondays", obs);
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
        Assert.Empty(store.Snapshot!.LongitudinalObservations);
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

        var stored = store.Snapshot!.LongitudinalObservations[0];
        // Date prefix "YYYY-MM-DD: " = 12 chars, then up to 200 chars of body.
        Assert.True(stored.Length <= 12 + LongitudinalObservationWriter.MaxObservationLength);
    }

    [Fact]
    public void Append_DuplicateOfRecent_Skipped()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        Assert.True(writer.Append("the user is feeling worse on weekends"));
        Assert.False(writer.Append("the user is feeling worse on weekends"));
        // Slightly different wording but same tokens → still dedup hit.
        Assert.False(writer.Append("the user is feeling worse on weekends now"));
        Assert.Single(store.Snapshot!.LongitudinalObservations);
    }

    [Fact]
    public void Append_DifferentObservation_AppendsBoth()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        Assert.True(writer.Append("symptoms worse on weekends"));
        Assert.True(writer.Append("user prefers tighter mask in evenings"));
        Assert.Equal(2, store.Snapshot!.LongitudinalObservations.Count);
    }

    [Fact]
    public void Append_BeyondCap_OldestEvicted()
    {
        var store = new FakeProfileStore();
        var writer = new LongitudinalObservationWriter(store);

        // 60 unique observations — 10 over the cap.
        for (int i = 0; i < 60; i++)
            writer.Append($"observation number {i} with unique tokens xyzzy_{i}");

        Assert.Equal(LongitudinalObservationWriter.MaxObservations,
                     store.Snapshot!.LongitudinalObservations.Count);
        // First 10 evicted; the surviving 50 start from "observation number 10".
        Assert.Contains(store.Snapshot.LongitudinalObservations,
                        o => o.Contains("xyzzy_10"));
        Assert.DoesNotContain(store.Snapshot.LongitudinalObservations,
                              o => o.Contains("xyzzy_0 "));
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
