using System;
using System.IO;
using System.Linq;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UM sprint PR-A storage layer: UserModelIndexStore (user_model_index.json) +
/// DailyBehaviorAggregateWriter (behavior_log.jsonl). Pins atomic round-trip,
/// zero-state defaults, the sprint-locked PII annotations, and the writer's
/// idempotent-by-date contract.
/// </summary>
public class UserModelStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _indexPath;
    private readonly string _logPath;
    private readonly string _profilePath;

    public UserModelStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-um-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _indexPath = Path.Combine(_tempDir, "user_model_index.json");
        _logPath = Path.Combine(_tempDir, "behavior_log.jsonl");
        _profilePath = Path.Combine(_tempDir, "habituation_profile.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── UM3: UserModelIndexStore ─────────────────────────────────────────────

    [Fact]
    public void LoadIndex_MissingFile_ReturnsZeroStateDefaults()
    {
        var store = new UserModelIndexStore(_indexPath);
        var idx = store.LoadIndex();

        Assert.NotNull(idx);
        Assert.Equal(1, idx.SchemaVersion);
        Assert.False(idx.Exportable);        // sprint-locked
        Assert.True(idx.DeletableOnClear);   // sprint-locked
        Assert.Equal(0, idx.LastAnalyzedChatTurnIndex);
        Assert.Empty(idx.BucketManifest);
    }

    [Fact]
    public void SaveThenLoad_Index_RoundTrips()
    {
        var store = new UserModelIndexStore(_indexPath);
        var idx = new UserModelIndex { LastAnalyzedChatTurnIndex = 42 };
        idx.BucketManifest["2026-06"] = new BucketMeta { EntryCount = 12, LastUpdated = "2026-06-07T14:22:00Z" };

        store.SaveIndex(idx);
        var loaded = store.LoadIndex();

        Assert.Equal(42, loaded.LastAnalyzedChatTurnIndex);
        Assert.True(loaded.BucketManifest.ContainsKey("2026-06"));
        Assert.Equal(12, loaded.BucketManifest["2026-06"].EntryCount);
        Assert.Equal("2026-06-07T14:22:00Z", loaded.BucketManifest["2026-06"].LastUpdated);
    }

    [Fact]
    public void SerializedIndex_UsesSnakeCaseAndCarriesPiiAnnotations()
    {
        var store = new UserModelIndexStore(_indexPath);
        store.SaveIndex(new UserModelIndex { LastAnalyzedChatTurnIndex = 3 });

        string raw = File.ReadAllText(_indexPath);
        Assert.Contains("\"schema_version\"", raw);
        Assert.Contains("\"last_analyzed_chat_turn_index\"", raw);
        Assert.Contains("\"bucket_manifest\"", raw);
        Assert.Contains("\"exportable\": false", raw);
        Assert.Contains("\"deletable_on_clear\": true", raw);
    }

    // ── UM1: DailyBehaviorAggregateWriter ────────────────────────────────────

    private static DailyBehaviorAggregate Row(string date, int sessionMinutes = 100) => new()
    {
        Date = date,
        SessionMinutes = sessionMinutes,
        OfiP50 = 38, OfiP90 = 71, TierModal = 4,
        SpikeCount = 1, StableWindowsCompleted = 3,
        OffsetDeltaPp = 2.5, MaskActivations = 7,
    };

    [Fact]
    public void Write_DifferentDates_Appends()
    {
        var w = new DailyBehaviorAggregateWriter(_logPath);
        w.Write(Row("2026-06-06"));
        w.Write(Row("2026-06-07"));

        var rows = w.ReadAll();
        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-06-06", rows[0].Date);
        Assert.Equal("2026-06-07", rows[1].Date);
    }

    [Fact]
    public void Write_SameDateTwice_UpdatesInPlace_NoDuplicate()
    {
        var w = new DailyBehaviorAggregateWriter(_logPath);
        w.Write(Row("2026-06-07", sessionMinutes: 100));
        w.Write(Row("2026-06-07", sessionMinutes: 142)); // same day, later in the day

        var rows = w.ReadAll();
        var same = rows.Where(r => r.Date == "2026-06-07").ToList();
        var only = Assert.Single(same);                 // idempotent by date
        Assert.Equal(142, only.SessionMinutes);          // last write wins
    }

    [Fact]
    public void Write_BlankDate_IsNoOp()
    {
        var w = new DailyBehaviorAggregateWriter(_logPath);
        w.Write(Row(""));
        Assert.Empty(w.ReadAll());
    }

    // ── LOGIC-06: atomic rewrite + path-keyed gate ───────────────────────────

    /// <summary>The same-day in-place update (which goes through the atomic full-file
    /// rewrite) preserves every prior day's row — the old File.WriteAllText already did,
    /// but this pins that the tmp + File.Replace rewrite did not regress it and produces
    /// no leftover .tmp artifact.</summary>
    [Fact]
    public void Write_SameDayUpdate_PreservesPriorDays_NoTmpLeftover()
    {
        var w = new DailyBehaviorAggregateWriter(_logPath);
        w.Write(Row("2026-06-05", sessionMinutes: 10));
        w.Write(Row("2026-06-06", sessionMinutes: 20));
        w.Write(Row("2026-06-07", sessionMinutes: 30));

        // Same-day rewrite of the middle day must keep 6/5 and 6/7 intact.
        w.Write(Row("2026-06-06", sessionMinutes: 99));

        var rows = w.ReadAll();
        Assert.Equal(3, rows.Count);
        Assert.Equal("2026-06-05", rows[0].Date);
        Assert.Equal(10, rows[0].SessionMinutes);
        Assert.Equal("2026-06-06", rows[1].Date);
        Assert.Equal(99, rows[1].SessionMinutes); // updated in place
        Assert.Equal("2026-06-07", rows[2].Date);
        Assert.Equal(30, rows[2].SessionMinutes);

        // The atomic rewrite must not leave a dangling tmp file behind.
        Assert.False(File.Exists(_logPath + ".tmp"));
    }

    /// <summary>Two writer instances over the SAME path share one lock (path-keyed gate),
    /// so concurrent same-day rewrites serialise and the file never tears: it always
    /// parses cleanly into exactly the days that were written.</summary>
    [Fact]
    public void ConcurrentWriters_SameFile_DoNotTear()
    {
        // Seed three distinct days so a torn rewrite (truncate-then-write) of one would
        // be observable as missing/garbled prior days.
        var seeder = new DailyBehaviorAggregateWriter(_logPath);
        seeder.Write(Row("2026-06-05"));
        seeder.Write(Row("2026-06-06"));
        seeder.Write(Row("2026-06-07"));

        var w1 = new DailyBehaviorAggregateWriter(_logPath);
        var w2 = new DailyBehaviorAggregateWriter(_logPath);
        var reader = new DailyBehaviorAggregateWriter(_logPath);

        var ex = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads = new System.Collections.Generic.List<System.Threading.Thread>();
        for (int t = 0; t < 4; t++)
        {
            var writer = (t % 2 == 0) ? w1 : w2;
            int mins = 100 + t;
            threads.Add(new System.Threading.Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        // Repeated same-day updates → every iteration is an atomic rewrite.
                        writer.Write(Row("2026-06-07", sessionMinutes: mins));
                        // Interleave reads; a torn file would deserialize to fewer than 3 rows.
                        var rows = reader.ReadAll();
                        Assert.True(rows.Count >= 3,
                            $"file tore: only {rows.Count} rows visible");
                    }
                }
                catch (Exception e) { ex.Enqueue(e); }
            }));
        }

        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();

        Assert.True(ex.IsEmpty, ex.TryDequeue(out var first) ? first.ToString() : "");

        // Final state: still exactly the three seeded days, none lost to a torn write.
        var final = reader.ReadAll();
        Assert.Equal(3, final.Count);
        Assert.Equal(new[] { "2026-06-05", "2026-06-06", "2026-06-07" },
                     final.Select(r => r.Date).ToArray());
        Assert.False(File.Exists(_logPath + ".tmp"));
    }

    // ── H24: ClearUserModel + observation access (PR-C) ──────────────────────

    [Fact]
    public void GetObservationBuckets_ReturnsProfileBuckets()
    {
        var profile = new FileHabituationProfileStore(_profilePath);
        var snap = new HabituationProfileSnapshot();
        snap.LongitudinalObservationsV2["2026-06"] =
            new List<ObservationEntry> { new() { Ts = "2026-06-07T00:00:00Z", Text = "x" } };
        profile.Save(snap);

        var store = new UserModelIndexStore(_indexPath, _logPath, profile);
        var buckets = store.GetObservationBuckets();

        Assert.True(buckets.ContainsKey("2026-06"));
        Assert.Single(buckets["2026-06"]);
    }

    [Fact]
    public void ClearUserModel_WipesBehaviorLogIndexObservations_PreservesStructuredDeltas()
    {
        // Arrange: a profile carrying observations AND structured deltas.
        var profile = new FileHabituationProfileStore(_profilePath);
        var snap = new HabituationProfileSnapshot
        {
            Profile = "SENSITIVE",
            SevereStrikeCount = 2,
            EngineState = "COOLDOWN",
            CalibrationCount = 3,
            PersonalPressureScale = 1.2,
            HabituationOffsetPp = 8.0, // a proxy for offset — must be untouched (Q4.5)
        };
        snap.LongitudinalObservationsV2["2026-06"] =
            new List<ObservationEntry> { new() { Ts = "2026-06-07T00:00:00Z", Text = "obs" } };
        snap.LongitudinalObservations.Add("legacy flat obs");
        profile.Save(snap);

        new DailyBehaviorAggregateWriter(_logPath).Write(Row("2026-06-07"));
        var store = new UserModelIndexStore(_indexPath, _logPath, profile);
        var seed = new UserModelIndex { LastAnalyzedChatTurnIndex = 5 };
        seed.BucketManifest["2026-06"] = new BucketMeta { EntryCount = 1 };
        store.SaveIndex(seed);
        Assert.NotEmpty(store.LoadIndex().BucketManifest); // precondition: non-empty before clear

        // Act
        store.ClearUserModel();

        // Assert: user-model files wiped.
        Assert.False(File.Exists(_logPath));
        var idx = store.LoadIndex();
        Assert.Equal(0, idx.LastAnalyzedChatTurnIndex);
        Assert.Empty(idx.BucketManifest);

        var after = profile.Load()!;
        Assert.Empty(after.LongitudinalObservationsV2);  // observation memory cleared
        Assert.Empty(after.LongitudinalObservations);

        // Structured deltas preserved (brief + Q4.5).
        Assert.Equal("SENSITIVE", after.Profile);
        Assert.Equal(2, after.SevereStrikeCount);
        Assert.Equal("COOLDOWN", after.EngineState);
        Assert.Equal(3, after.CalibrationCount);
        Assert.Equal(1.2, after.PersonalPressureScale);
        Assert.Equal(8.0, after.HabituationOffsetPp); // Q4.5: offset untouched
    }

    [Fact]
    public void ClearUserModel_IndexOnly_NoProfileOrLog_DoesNotThrow()
    {
        // The index-only ctor (no behaviour-log path, no profile store) must still
        // reset the index without throwing.
        var store = new UserModelIndexStore(_indexPath);
        store.SaveIndex(new UserModelIndex { LastAnalyzedChatTurnIndex = 9 });

        store.ClearUserModel();

        Assert.Equal(0, store.LoadIndex().LastAnalyzedChatTurnIndex);
    }

    [Fact]
    public void SerializedRow_UsesSnakeCaseAndCarriesPiiAnnotations()
    {
        var w = new DailyBehaviorAggregateWriter(_logPath);
        w.Write(Row("2026-06-07"));

        string raw = File.ReadAllText(_logPath).Trim();
        Assert.Contains("\"date\":\"2026-06-07\"", raw.Replace(" ", ""));
        Assert.Contains("\"session_minutes\"", raw);
        Assert.Contains("\"ofi_p50\"", raw);
        Assert.Contains("\"ofi_p90\"", raw);
        Assert.Contains("\"tier_modal\"", raw);
        Assert.Contains("\"spike_count\"", raw);
        Assert.Contains("\"stable_windows_completed\"", raw);
        Assert.Contains("\"offset_delta_pp\"", raw);
        Assert.Contains("\"mask_activations\"", raw);
        Assert.Contains("\"exportable\":false", raw.Replace(" ", ""));
        Assert.Contains("\"deletable_on_clear\":true", raw.Replace(" ", ""));
    }
}
