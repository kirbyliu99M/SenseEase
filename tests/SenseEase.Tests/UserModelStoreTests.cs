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

    public UserModelStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-um-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _indexPath = Path.Combine(_tempDir, "user_model_index.json");
        _logPath = Path.Combine(_tempDir, "behavior_log.jsonl");
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
