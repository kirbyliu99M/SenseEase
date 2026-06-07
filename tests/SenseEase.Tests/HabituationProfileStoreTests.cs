using System;
using System.IO;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Phase 4 + Stage 6 contract tests for FileHabituationProfileStore +
/// AtomicJsonWriter. Covers schema v2 round-trip, snake_case wire format,
/// schema-version mismatch fall-back (Q2), atomic-write recovery from a
/// prior-crash .tmp residue, v1 → v2 in-memory null-fill, and the Stage 6c
/// app-data-wipe edge case.
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
        // SchemaVersion: rely on the property's default initialiser (= 2 after
        // Stage 6). Tests must not hard-code the version literal; the snapshot
        // DTO is the single source of truth.
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
        Assert.Equal(3, loaded!.SchemaVersion);
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

        // Stage 6 v2 field names — present on every snapshot write because the
        // properties are non-nullable value types / property-initialised lists.
        // Nullable enum/string fields (calibration_completeness, calibration_path,
        // calibration_responses, last_calibration_at_iso, symptom_dominance_primary,
        // medical_advisory_shown, calibration_confidence) may be omitted by the
        // default serializer when null, so we don't assert their presence here.
        Assert.Contains("\"calibration_count\"", raw);
        Assert.Contains("\"longitudinal_observations\"", raw);
        Assert.Contains("\"personal_pressure_scale\"", raw);
        Assert.Contains("\"was_recovered_from_wipe\"", raw);
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
        Assert.Equal(3, loaded.SchemaVersion);
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

    // ── Stage 6: v1 → v2 in-memory null-fill ─────────────────────────────────
    // A v1 file on disk loads as a v2-shaped in-memory snapshot with the new
    // fields at their CLR / property-initialiser defaults. The on-disk file
    // stays v1 until the next Save upgrades it.

    /// <summary>Hand-crafted v1 JSON (just the 17 original v1 fields +
    /// schema_version: 1). The writer would never emit this shape today
    /// because the snapshot DTO has v2 fields, but a real v1 file on disk
    /// from before the upgrade looks exactly like this.</summary>
    private const string LegacyV1Json = """
        {
          "schema_version": 1,
          "updated_at_iso": "2026-05-24T12:34:56.0000000Z",
          "profile": "TYPICAL",
          "profile_source": "default",
          "habituation_level": 12.5,
          "habituation_offset_pp": 3.0,
          "habituation_offset_cap_pp": 12,
          "current_vims_tier": 2,
          "severe_strike_count": 0.0,
          "severe_strike_limit": 2.0,
          "engine_state": "RUNNING",
          "externally_paused": false,
          "awaiting_user_confirm": false,
          "last_spike_at_iso": null,
          "next_step_eligible_at_iso": null,
          "params_snapshot": {
            "stable_window_ms": 240000,
            "step_interval_ms": 60000,
            "step_magnitude_pp": 1.0
          }
        }
        """;

    [Fact]
    public void LoadLegacyV1AsV2InMemory_PopulatesDefaultsForNewFields()
    {
        File.WriteAllText(_path, LegacyV1Json);

        var store = new FileHabituationProfileStore(_path);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        // In-memory shape is v2 — loader bumped the version.
        Assert.Equal(2, loaded!.SchemaVersion);

        // v1 fields survived the round-trip.
        Assert.Equal("TYPICAL", loaded.Profile);
        Assert.Equal(12.5, loaded.HabituationLevel);
        Assert.Equal(60_000, loaded.ParamsSnapshot.StepIntervalMs);

        // v2 fields at their defaults.
        Assert.Equal(0, loaded.CalibrationCount);
        Assert.Null(loaded.LastCalibrationAtIso);
        Assert.Null(loaded.CalibrationCompleteness);
        Assert.Null(loaded.CalibrationPath);
        Assert.Null(loaded.CalibrationResponses);
        Assert.NotNull(loaded.LongitudinalObservations);
        Assert.Empty(loaded.LongitudinalObservations);
        Assert.Equal(1.0, loaded.PersonalPressureScale);
        Assert.Null(loaded.SymptomDominancePrimary);
        Assert.Null(loaded.MedicalAdvisoryShown);
        Assert.Null(loaded.CalibrationConfidence);
        Assert.False(loaded.WasRecoveredFromWipe);
    }

    [Fact]
    public void LoadLegacyV1AsV2InMemory_NextWriteUpgradesFileToV2()
    {
        File.WriteAllText(_path, LegacyV1Json);

        var store = new FileHabituationProfileStore(_path);
        var loaded = store.Load();
        Assert.NotNull(loaded);

        // Round-trip: save it straight back.
        store.Save(loaded!);

        string raw = File.ReadAllText(_path);
        // schema_version on disk is now 2 (the snapshot DTO's default + the
        // loader's in-memory bump are both consistent).
        Assert.Contains("\"schema_version\": 2", raw);
        // v2 fields are now persisted (with their defaults).
        Assert.Contains("\"calibration_count\"", raw);
        Assert.Contains("\"longitudinal_observations\"", raw);
        Assert.Contains("\"personal_pressure_scale\"", raw);
        Assert.Contains("\"was_recovered_from_wipe\"", raw);
    }

    // ── UM2: v2 → v3 monthly-bucket migration ────────────────────────────────

    [Fact]
    public void Load_V2File_MigratesFlatObservationsIntoCurrentMonthBucket()
    {
        const string v2Json = """
            {
              "schema_version": 2,
              "profile": "TYPICAL",
              "longitudinal_observations": ["obs alpha unique", "obs bravo unique"]
            }
            """;
        File.WriteAllText(_path, v2Json);

        var store = new FileHabituationProfileStore(_path);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.SchemaVersion);          // migrated to v3
        Assert.Empty(loaded.LongitudinalObservations);   // flat list cleared after migration

        string month = DateTime.UtcNow.ToString("yyyy-MM");
        Assert.True(loaded.LongitudinalObservationsV2.ContainsKey(month));
        Assert.Equal(2, loaded.LongitudinalObservationsV2[month].Count);
        Assert.Contains(loaded.LongitudinalObservationsV2[month], e => e.Text == "obs alpha unique");
    }

    [Fact]
    public void Load_V2File_MigrationCapsCurrentMonthBucketAt30_OldestDropped()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append("obs number ").Append(i).Append(" xyzzy_").Append(i).Append('"');
        }
        string v2Json =
            "{ \"schema_version\": 2, \"profile\": \"TYPICAL\", \"longitudinal_observations\": ["
            + sb + "] }";
        File.WriteAllText(_path, v2Json);

        var store = new FileHabituationProfileStore(_path);
        var loaded = store.Load();

        string month = DateTime.UtcNow.ToString("yyyy-MM");
        var bucket = loaded!.LongitudinalObservationsV2[month];
        Assert.Equal(30, bucket.Count);                              // capped at 30
        Assert.Contains(bucket, e => e.Text.Contains("xyzzy_39"));   // newest kept
        Assert.DoesNotContain(bucket, e => e.Text.Contains("xyzzy_0")); // i=0 dropped (oldest)
    }

    [Fact]
    public void Load_V3File_ReturnedAsIs_NoMigration()
    {
        var store = new FileHabituationProfileStore(_path);
        var snap = MakeSnapshot(); // SchemaVersion default = 3
        snap.LongitudinalObservationsV2["2026-06"] = new System.Collections.Generic.List<ObservationEntry>
        {
            new() { Ts = "2026-06-07T00:00:00Z", Text = "kept verbatim" },
        };
        store.Save(snap);

        var loaded = store.Load();
        Assert.Equal(3, loaded!.SchemaVersion);
        Assert.Single(loaded.LongitudinalObservationsV2["2026-06"]);
        Assert.Equal("kept verbatim", loaded.LongitudinalObservationsV2["2026-06"][0].Text);
    }

    // ── H14: schema-downgrade guard ──────────────────────────────────────────

    [Fact]
    public void Load_V1Downgrade_AfterV2FlagSet_Refused()
    {
        var store = new FileHabituationProfileStore(_path);
        store.Save(MakeSnapshot());                  // v2 write drops the schema-floor flag
        Assert.True(File.Exists(Path.Combine(_tempDir, "schema_floor_v2.flag")));
        Assert.NotNull(store.Load());                // v2 still loads

        // Attacker hand-edits the file back to schema_version:1.
        File.WriteAllText(_path, LegacyV1Json);
        Assert.Null(store.Load());                   // H14: refused (flag present) → defaults
    }

    [Fact]
    public void Load_V1WithRecentDate_NoFlag_StillLoads()
    {
        // review #4/#5: the lossy/​bypassable date arm was removed. A genuine pre-v2
        // file (no flag) must load regardless of its updated_at_iso — even a recent
        // date that the old date-guard would have wrongly rejected as a downgrade.
        string recentV1 = LegacyV1Json.Replace(
            "2026-05-24T12:34:56.0000000Z", "2026-07-01T00:00:00.0000000Z");
        File.WriteAllText(_path, recentV1);

        var store = new FileHabituationProfileStore(_path);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("TYPICAL", loaded!.Profile);
    }

    // ── Stage 6c: app-data-wipe edge case ────────────────────────────────────
    // When habituation_profile.json is missing but habituation.json (or any
    // other configured "prior usage" artifact) exists, the loader returns a
    // synthesized defaults-only snapshot with WasRecoveredFromWipe=true.
    // Genuine first-runs (no artifacts at all) still return null. A real
    // profile file shadows the wipe path entirely.

    [Fact]
    public void WasRecoveredFromWipe_DetectsHabituationJsonWithoutProfileJson()
    {
        // Only the legacy offset-only habituation.json exists.
        string habituationPath = Path.Combine(_tempDir, "habituation.json");
        File.WriteAllText(habituationPath, "{\"offset\":0.0}");
        // habituation_profile.json deliberately NOT created.

        var store = new FileHabituationProfileStore(_path, habituationPath);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.True(loaded!.WasRecoveredFromWipe,
            "Wipe-recovery flag should be set when probe artifact survives a profile-file wipe.");
        Assert.Equal(3, loaded.SchemaVersion);
        // Defaults — no calibration, RESILIENT-equivalent.
        Assert.Equal(0, loaded.CalibrationCount);
        Assert.Equal(1.0, loaded.PersonalPressureScale);
        Assert.Empty(loaded.LongitudinalObservations);
    }

    [Fact]
    public void WasRecoveredFromWipe_FalseOnGenuineFirstRun()
    {
        // Neither file exists. Probe path passed but its target doesn't exist.
        string habituationPath = Path.Combine(_tempDir, "habituation.json");
        Assert.False(File.Exists(habituationPath));
        Assert.False(File.Exists(_path));

        var store = new FileHabituationProfileStore(_path, habituationPath);
        var loaded = store.Load();

        // Genuine first-run: caller treats null as "use defaults".
        Assert.Null(loaded);
    }

    [Fact]
    public void WasRecoveredFromWipe_FalseWhenProfileJsonExists()
    {
        // Profile file exists AND probe artifact exists. Profile file wins —
        // there's nothing to recover from.
        string habituationPath = Path.Combine(_tempDir, "habituation.json");
        File.WriteAllText(habituationPath, "{\"offset\":0.0}");

        var store = new FileHabituationProfileStore(_path, habituationPath);
        store.Save(MakeSnapshot());

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.False(loaded!.WasRecoveredFromWipe,
            "Wipe-recovery flag must NOT be set when the profile JSON itself loaded cleanly.");
        Assert.Equal("SENSITIVE", loaded.Profile);
    }

    [Fact]
    public void Save_ClearsWasRecoveredFromWipeFlag()
    {
        // The flag is one-shot: set by the loader, cleared by the next Save.
        // Even if upstream code keeps the synthesized snapshot in hand and
        // saves it back as-is, the on-disk file does NOT pretend the wipe
        // recovery state is real.
        string habituationPath = Path.Combine(_tempDir, "habituation.json");
        File.WriteAllText(habituationPath, "{\"offset\":0.0}");

        var store = new FileHabituationProfileStore(_path, habituationPath);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded!.WasRecoveredFromWipe);

        store.Save(loaded);

        string raw = File.ReadAllText(_path);
        Assert.Contains("\"was_recovered_from_wipe\": false", raw);
    }

    // ── W2.0-C: atomic read-modify-write (Update) ────────────────────────────
    // The engine's profile-snapshot persist and the chatbot's
    // LongitudinalObservationWriter write the SAME habituation_profile.json from
    // different threads. Update() must hold the store lock across the whole
    // Load→mutate→Save span so concurrent writers cannot lose each other's updates.

    [Fact]
    public void Update_PersistsReturnedSnapshot()
    {
        IHabituationProfileStore store = new FileHabituationProfileStore(_path);
        store.Save(MakeSnapshot());

        store.Update(s =>
        {
            Assert.NotNull(s);          // Update feeds the mutator the current snapshot
            s!.SevereStrikeCount = 9.0;
            return s;
        });

        Assert.Equal(9.0, store.Load()!.SevereStrikeCount);
    }

    [Fact]
    public void Update_MutatorReturnsNull_SkipsSave()
    {
        IHabituationProfileStore store = new FileHabituationProfileStore(_path);
        var seed = MakeSnapshot();
        seed.SevereStrikeCount = 3.0;
        store.Save(seed);

        // A null result is the "transient read failure / dedup no-op" signal —
        // Update must NOT write, leaving the on-disk snapshot untouched.
        store.Update(_ => null);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(3.0, loaded!.SevereStrikeCount);
    }

    [Fact]
    public void Update_ConcurrentReadModifyWrite_NoLostUpdates()
    {
        // Regression for the W2.0-C lost-update race. Many threads each do a
        // read-modify-write increment through Update(); every increment must land.
        // With only the per-method Load/Save locks (the pre-W2.0-C behaviour —
        // i.e. the non-atomic default Update) interleaved reads would clobber each
        // other and the final count would fall short of the op count.
        IHabituationProfileStore store = new FileHabituationProfileStore(_path);
        var seed = MakeSnapshot();
        seed.SevereStrikeCount = 0.0;
        store.Save(seed);

        const int ops = 200;
        System.Threading.Tasks.Parallel.For(0, ops, _ =>
            store.Update(s =>
            {
                if (s is null) return null;
                s.SevereStrikeCount += 1.0;
                return s;
            }));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal((double)ops, loaded!.SevereStrikeCount);
    }
}
