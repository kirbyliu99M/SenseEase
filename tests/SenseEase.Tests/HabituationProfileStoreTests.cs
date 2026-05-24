using System;
using System.IO;
using System.Text.Json;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Phase 4 contract tests for FileHabituationProfileStore +
/// AtomicJsonWriter. Covers schema v1 round-trip, snake_case wire format,
/// schema-version mismatch fall-back (Q2), and atomic-write recovery from a
/// prior-crash .tmp residue.
///
/// The Process.Kill mid-write physical smoke test is documented in
/// docs/HABITUATION-PROFILE.md §5.5 — it requires a separate process harness
/// and is run manually once during Phase 4 acceptance, not in CI.
/// </summary>
public class HabituationProfileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public HabituationProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "senseease-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "habituation_profile.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private HabituationProfileSnapshot MakeSnapshot() => new()
    {
        SchemaVersion           = 1,
        UpdatedAtIso            = "2026-05-24T12:34:56.0000000Z",
        Profile                 = "SENSITIVE",
        ProfileSource           = "calibration_demo",
        HabituationLevel        = 23.5,
        HabituationOffsetPp     = 8.0,
        HabituationOffsetCapPp  = 15,
        CurrentVimsTier         = 4,
        SevereStrikeCount       = 0.5,
        SevereStrikeLimit       = 1.0,
        EngineState             = "RUNNING",
        ExternallyPaused        = false,
        AwaitingUserConfirm     = false,
        LastSpikeAtIso          = "2026-05-24T12:30:11.0000000Z",
        NextStepEligibleAtIso   = null,
        ParamsSnapshot          = new HabituationParamsSnapshot
        {
            StableWindowMs  = 360_000,
            StepIntervalMs  =  60_000,
            StepMagnitudePp =      1.0,
        },
    };

    // ── Round-trip: write → read → equal ─────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var store = new FileHabituationProfileStore(_path);
        var snap = MakeSnapshot();

        store.Save(snap);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.SchemaVersion);
        Assert.Equal("SENSITIVE", loaded.Profile);
        Assert.Equal("calibration_demo", loaded.ProfileSource);
        Assert.Equal(23.5, loaded.HabituationLevel);
        Assert.Equal(8.0,  loaded.HabituationOffsetPp);
        Assert.Equal(15,   loaded.HabituationOffsetCapPp);
        Assert.Equal(4,    loaded.CurrentVimsTier);
        Assert.Equal(0.5,  loaded.SevereStrikeCount);
        Assert.Equal(1.0,  loaded.SevereStrikeLimit);
        Assert.Equal("RUNNING", loaded.EngineState);
        Assert.False(loaded.ExternallyPaused);
        Assert.False(loaded.AwaitingUserConfirm);
        Assert.Equal("2026-05-24T12:30:11.0000000Z", loaded.LastSpikeAtIso);
        Assert.Null(loaded.NextStepEligibleAtIso);
        Assert.Equal(360_000, loaded.ParamsSnapshot.StableWindowMs);
        Assert.Equal( 60_000, loaded.ParamsSnapshot.StepIntervalMs);
        Assert.Equal(    1.0, loaded.ParamsSnapshot.StepMagnitudePp);
    }

    // ── Snake_case wire format check ─────────────────────────────────────────

    [Fact]
    public void SerializedJson_UsesSnakeCaseFieldNames()
    {
        var store = new FileHabituationProfileStore(_path);
        store.Save(MakeSnapshot());

        string raw = File.ReadAllText(_path);
        // Field names spec'd in docs/HABITUATION-PROFILE.md §5 must appear as-is.
        Assert.Contains("\"schema_version\"", raw);
        Assert.Contains("\"updated_at_iso\"", raw);
        Assert.Contains("\"profile\"", raw);
        Assert.Contains("\"profile_source\"", raw);
        Assert.Contains("\"habituation_level\"", raw);
        Assert.Contains("\"habituation_offset_pp\"", raw);
        Assert.Contains("\"habituation_offset_cap_pp\"", raw);
        Assert.Contains("\"current_vims_tier\"", raw);
        Assert.Contains("\"severe_strike_count\"", raw);
        Assert.Contains("\"severe_strike_limit\"", raw);
        Assert.Contains("\"engine_state\"", raw);
        Assert.Contains("\"externally_paused\"", raw);
        Assert.Contains("\"awaiting_user_confirm\"", raw);
        Assert.Contains("\"last_spike_at_iso\"", raw);
        Assert.Contains("\"next_step_eligible_at_iso\"", raw);
        Assert.Contains("\"params_snapshot\"", raw);
        Assert.Contains("\"stable_window_ms\"", raw);
        Assert.Contains("\"step_interval_ms\"", raw);
        Assert.Contains("\"step_magnitude_pp\"", raw);
    }

    // ── Schema-version mismatch → null (Q2 fall-back) ───────────────────────

    [Fact]
    public void Load_UnknownSchemaVersion_ReturnsNull()
    {
        // Write a snapshot with schema_version = 99 manually.
        var bogus = MakeSnapshot();
        bogus.SchemaVersion = 99;
        var rawOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        File.WriteAllText(_path, JsonSerializer.Serialize(bogus, rawOpts));

        var store = new FileHabituationProfileStore(_path);
        var loaded = store.Load();

        // Q2 fall-back: unknown version → null (caller defaults to RESILIENT).
        // System.Diagnostics.Debug.WriteLine fires inside Load() — silent in
        // a test run, visible only under a debugger attach.
        Assert.Null(loaded);
    }

    // ── Missing file → null ──────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new FileHabituationProfileStore(_path);
        Assert.Null(store.Load());
    }

    // ── Multiple writes leave no .tmp residue ────────────────────────────────

    [Fact]
    public void Save_RepeatedWrites_LeaveNoTmpResidue()
    {
        var store = new FileHabituationProfileStore(_path);
        for (int i = 0; i < 5; i++)
        {
            var s = MakeSnapshot();
            s.HabituationLevel = i;
            store.Save(s);
        }
        // After successful writes, no .tmp file should remain on disk.
        Assert.False(File.Exists(_path + ".tmp"),
            "Repeated Save() left a .tmp residue file; atomic-rename contract violated.");
        Assert.True(File.Exists(_path));
    }

    // ── Recovery from a prior-crash .tmp residue ─────────────────────────────

    [Fact]
    public void Save_AfterPriorCrashTmpResidue_OverwritesAndClearsTmp()
    {
        // Simulate a previous run that crashed mid-write: a .tmp file exists
        // but the destination either doesn't (first-write crash) or has the
        // OLD content (mid-rename crash). Test both branches.

        // Branch 1: tmp residue, no destination (first-write crash). Subsequent
        // Save must complete successfully.
        File.WriteAllText(_path + ".tmp", "{garbage from previous crash");
        var store = new FileHabituationProfileStore(_path);
        store.Save(MakeSnapshot());

        Assert.False(File.Exists(_path + ".tmp"),
            "Save() did not clean up tmp residue from a prior-crash scenario.");
        Assert.True(File.Exists(_path));
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("SENSITIVE", loaded!.Profile);

        // Branch 2: destination exists from the successful save above; a fresh
        // .tmp residue from yet another crash. New Save must atomic-replace.
        File.WriteAllText(_path + ".tmp", "{garbage again");
        var s2 = MakeSnapshot();
        s2.Profile = "TYPICAL";
        store.Save(s2);

        Assert.False(File.Exists(_path + ".tmp"));
        loaded = store.Load();
        Assert.Equal("TYPICAL", loaded!.Profile);
    }

    // ── AtomicJsonWriter contract direct ─────────────────────────────────────

    [Fact]
    public void AtomicJsonWriter_TryWrite_ReturnsTrueOnSuccess()
    {
        var snap = MakeSnapshot();
        bool ok = AtomicJsonWriter.TryWrite(_path, snap);
        Assert.True(ok);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void AtomicJsonWriter_TryRead_NullForMissingFile()
    {
        var result = AtomicJsonWriter.TryRead<HabituationProfileSnapshot>(_path);
        Assert.Null(result);
    }

    [Fact]
    public void AtomicJsonWriter_TryRead_NullForMalformedJson()
    {
        File.WriteAllText(_path, "{this is not valid json");
        var result = AtomicJsonWriter.TryRead<HabituationProfileSnapshot>(_path);
        Assert.Null(result);
    }

    // ── Integration: MockInferenceEngine → store wiring ──────────────────────
    // Proves the end-to-end path (Phase 4d triggers) actually writes the file.
    // No production-scale assertions on the snapshot CONTENT here; the field-
    // mapping tests above cover that. This test only confirms the trigger
    // fires and the snapshot lands on disk via the store.

    [Fact]
    public void MockInferenceEngine_WithProfileStore_WritesSnapshotOnConstruction()
    {
        // Constructor sets _profileSnapshotDirty=true when there's no existing
        // snapshot file (gotcha #1: legacy / first-install upgrade path).
        // The first Update tick should then trigger a write.
        var store = new FileHabituationProfileStore(_path);
        var engine = new MockInferenceEngine(profileStore: store);
        engine.SetHabituationPaused(false); // un-pause so Update does work

        engine.Update(50.0, "Passenger");

        Assert.True(File.Exists(_path),
            "MockInferenceEngine.Update did not trigger a profile-snapshot write.");
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("RESILIENT", loaded!.Profile);
        Assert.Equal("default", loaded.ProfileSource);
        Assert.Equal(1, loaded.SchemaVersion);
    }

    [Fact]
    public void MockInferenceEngine_SetHabituationProfile_WritesSnapshot()
    {
        var store = new FileHabituationProfileStore(_path);
        var engine = new MockInferenceEngine(profileStore: store);

        engine.SetHabituationProfile(HabituationProfile.Sensitive, "calibration_demo");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("SENSITIVE", loaded!.Profile);
        Assert.Equal("calibration_demo", loaded.ProfileSource);
    }
}
