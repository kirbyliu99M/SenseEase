using System;
using System.Collections.Generic;
using System.Diagnostics;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>File-backed habituation-profile snapshot store. Atomic write via
/// <see cref="AtomicJsonWriter"/> (tmp + File.Replace). Unknown
/// schema_version → null + Debug.WriteLine warning per Q2 fall-back.
/// <para>Stage 6 (schema v2): writer always emits schema_version=2. Reader
/// accepts 2 directly and accepts 1 by null-filling the new v2 fields
/// (see <see cref="LoadLegacyV1AsV2InMemory"/>). The file on disk stays v1
/// until the next Save upgrades it to v2.</para>
/// <para>Stage 6c app-data-wipe edge case: if habituation_profile.json is
/// missing but a sibling artifact (currently habituation.json — passed via
/// <see cref="WipeProbeArtifactPath"/>) exists, Load synthesizes a default
/// snapshot with <see cref="HabituationProfileSnapshot.WasRecoveredFromWipe"/>
/// = true so upstream code knows "user has used this app before; treat
/// calibration trigger semantics accordingly".</para>
/// See docs/HABITUATION-PROFILE.md §5 for the full schema and write triggers.</summary>
public sealed class FileHabituationProfileStore : IHabituationProfileStore
{
    private readonly string _path;
    private readonly string? _wipeProbePath;

    // W2.0-C hardening (DIM-3 review): the read-modify-write lock is keyed by FILE
    // PATH, not per-instance, so EVERY FileHabituationProfileStore for the same file
    // serialises — the engine's shared instance, the analyser's, AND a fresh instance
    // created elsewhere (e.g. the Settings "clear all my data" button). A per-instance
    // lock would let those race on habituation_profile.json (lost update / partial wipe).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _pathGates
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private const int SupportedSchemaVersion = 3; // UM2: monthly observation buckets
    private const int SchemaVersionV2 = 2;        // flat longitudinal_observations FIFO
    private const int LegacyV1SchemaVersion = 1;

    // H14: schema-downgrade guard. Editing the file to {"schema_version":1,...}
    // null-fills MedicalAdvisoryShown and bypasses the vestibular safety intercept.
    // Once this profile has been written as v2 we drop a sibling flag; its presence
    // marks a v1 load as a downgrade, which we refuse — falling back to RESILIENT
    // defaults is safer than a null-filled advisory. (A date-based second arm was
    // removed per review #4/#5: lossy on genuine old files + timezone-bypassable.)
    private readonly string _schemaFloorFlagPath;

    /// <summary>Constructor with optional wipe-probe artifact path. When the
    /// profile JSON is missing but the artifact at this path exists, Load
    /// returns a wipe-recovery snapshot instead of null. Pass
    /// <c>AppConfig.HabituationStatePath</c> to enable the wipe-detection
    /// path; pass <c>null</c> (default) to disable it (e.g. in tests that
    /// don't exercise the wipe scenario).</summary>
    public FileHabituationProfileStore(string path, string? wipeProbeArtifactPath = null)
    {
        _path = path;
        _gate = _pathGates.GetOrAdd(GateKey(path), static _ => new object());
        _wipeProbePath = wipeProbeArtifactPath;
        // Sibling of the profile file (one profile per install) so the flag is
        // co-located with the data it guards.
        _schemaFloorFlagPath = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty, "schema_floor_v2.flag");
    }

    /// <summary>The probe path the loader consults for wipe-detection. Null if
    /// not configured. Exposed for diagnostic / test purposes only.</summary>
    public string? WipeProbeArtifactPath => _wipeProbePath;

    public HabituationProfileSnapshot? Load()
    {
        lock (_gate)
        {
            // H50: reject a reparse-pointed / base-escaping profile path before
            // any File.* op. Base dir is derived from _path (no hardcoding); a
            // normal profile file — or a not-yet-created one on first run, incl.
            // the test suite's temp sandbox — passes the guard unchanged.
            GuardPath();
            var snap = AtomicJsonWriter.TryRead<HabituationProfileSnapshot>(_path);
            if (snap is null)
            {
                // Stage 6c: app-data-wipe edge case. The profile JSON is gone
                // but a sibling artifact suggesting prior usage (currently
                // habituation.json) survives → synthesize a defaults-only
                // snapshot with WasRecoveredFromWipe=true. Genuine first-run
                // (no probe artifact either) still returns null.
                if (_wipeProbePath is not null && File.Exists(_wipeProbePath))
                {
                    Debug.WriteLine(
                        $"[HabituationProfile] {_path} missing but probe artifact " +
                        $"{_wipeProbePath} exists; returning wipe-recovery snapshot.");
                    return new HabituationProfileSnapshot
                    {
                        // SchemaVersion default is 2 (Stage 6). All other fields
                        // stay at their CLR / property-initializer defaults.
                        WasRecoveredFromWipe = true,
                    };
                }
                return null;
            }

            // Q2 schema-version fall-back. Unknown version → null + diagnostic.
            // We deliberately do NOT open a separate engine.log here; per Opus
            // Option 1, Debug.WriteLine is visible to an attached debugger and
            // silent in prod, which is the right cost/benefit for this
            // expected-rare path.
            switch (snap.SchemaVersion)
            {
                case SupportedSchemaVersion:
                    return snap;

                case SchemaVersionV2:
                    // UM2: migrate the flat v2 observation FIFO into the v3 monthly
                    // bucket structure. Lossless (entries move; only the >30-per-bucket
                    // tail is dropped) and touches no safety field. The on-disk file
                    // upgrades to v3 on the next Save.
                    return MigrateV2ToV3(snap);

                case LegacyV1SchemaVersion:
                    // Stage 6b: deserialise a v1 file. JsonSerializer already
                    // filled v1 fields; the v2 fields stayed at their CLR /
                    // property-initialiser defaults (CalibrationCount=0,
                    // PersonalPressureScale=1.0, LongitudinalObservations=[],
                    // nullable enums = null). Bump the in-memory schema_version
                    // to 2 so downstream code uniformly sees v2 shape. The on-
                    // disk file stays v1 until the next Save.
                    //
                    // Intentionally NOT called "migration" — there is no
                    // transformation logic, just null-fill at deserialise time.
                    // Renaming this method to "Migrate*" would mislead future
                    // readers into expecting per-field translation rules.
                    if (IsSchemaDowngrade())
                    {
                        Debug.WriteLine(
                            $"[HabituationProfile] refusing v1 downgrade of {_path} " +
                            "(schema_floor_v2 flag present or updated_at_iso post-dates v2).");
                        return null;
                    }
                    return LoadLegacyV1AsV2InMemory(snap);

                default:
                    Debug.WriteLine(
                        $"[HabituationProfile] Unknown schema_version {snap.SchemaVersion} " +
                        $"in {_path}; falling back to RESILIENT defaults.");
                    return null;
            }
        }
    }

    /// <summary>v1 → v2 in-memory null-fill. NOT a migration: no field
    /// transformation happens; v2 fields are already at their default values
    /// after deserialisation, this just bumps SchemaVersion so the rest of the
    /// app uniformly sees v2 shape. The on-disk file stays v1 until the next
    /// Save upgrades it.</summary>
    private static HabituationProfileSnapshot LoadLegacyV1AsV2InMemory(HabituationProfileSnapshot snap)
    {
        // UM2: v1 → v2 (NOT v3). v1 files carry no observations, so there is nothing
        // to bucket; they stay on the unchanged v2 shape and reach v3 on a later load
        // once written as v2. Explicit (not SupportedSchemaVersion) so this stays v2
        // even as the supported version advances.
        snap.SchemaVersion = SchemaVersionV2;
        // Defensive: explicit null-fill in case a future v2-field property
        // initialiser changes and a v1 deserialisation path leaves something
        // in an unexpected state. CLR defaults already cover this but the
        // explicit assignment documents the contract.
        snap.LongitudinalObservations ??= new();
        snap.LongitudinalObservationsV2 ??= new();
        return snap;
    }

    /// <summary>UM2: migrate a v2 snapshot (flat <c>longitudinal_observations</c>
    /// FIFO) to v3 (monthly buckets). All flat entries move into the CURRENT month's
    /// bucket, capped at <see cref="LongitudinalObservationWriter.MaxObservationsPerBucket"/>
    /// (oldest dropped); the flat list is then cleared. No safety field is touched —
    /// only the observation memory is restructured. The on-disk file upgrades to v3
    /// on the next Save.</summary>
    private static HabituationProfileSnapshot MigrateV2ToV3(HabituationProfileSnapshot snap)
    {
        snap.LongitudinalObservationsV2 ??= new();
        if (snap.LongitudinalObservations is { Count: > 0 })
        {
            string month = DateTime.UtcNow.ToString("yyyy-MM");
            if (!snap.LongitudinalObservationsV2.TryGetValue(month, out var bucket))
            {
                bucket = new List<ObservationEntry>();
                snap.LongitudinalObservationsV2[month] = bucket;
            }
            foreach (string text in snap.LongitudinalObservations)
                // DIM-2 review: strip the legacy "yyyy-MM-dd: " stamp so a migrated entry's
                // Text matches the clean body new entries store (the date was redundant
                // with the new Ts field and would otherwise double-stamp the LLM context).
                bucket.Add(new ObservationEntry
                {
                    Ts = DateTime.UtcNow.ToString("o"),
                    Text = LongitudinalObservationWriter.StripDateStamp(text),
                });
            while (bucket.Count > LongitudinalObservationWriter.MaxObservationsPerBucket)
                bucket.RemoveAt(0);
            snap.LongitudinalObservations.Clear();
        }
        snap.SchemaVersion = SupportedSchemaVersion; // 3
        return snap;
    }

    /// <summary>H14: a v1 file is a downgrade attack if this install has already
    /// written a v2 file (flag present) OR the v1 file claims an updated_at_iso at
    /// or after the v2 schema ship date (no genuine v1 file is written after v2
    /// started shipping).</summary>
    private bool IsSchemaDowngrade()
    {
        // review #4/#5: the date-based arm was both lossy (rejected genuine pre-v2
        // files dated after the constant) and bypassable (Unspecified-kind / blank
        // timestamps). The schema_floor_v2 flag is the robust signal — present iff
        // THIS install has already written v2, so a later v1 file is a downgrade.
        try { return File.Exists(_schemaFloorFlagPath); } catch { return false; }
    }

    /// <summary>W2.0-C: atomic read-modify-write. Holds <see cref="_gate"/>
    /// across the entire Load→mutate→Save span so a concurrent writer of the
    /// same file (the engine's profile persist vs the chatbot's
    /// <c>LongitudinalObservationWriter</c>) cannot interleave between our read
    /// and our write and lose an update. The per-method locks on
    /// <see cref="Load"/>/<see cref="Save"/> only prevent torn individual ops;
    /// the lost-update race is across the SPAN, which only this override closes.
    /// <para>Reentrant by design: the inner <see cref="Load"/>/<see cref="Save"/>
    /// calls re-acquire <see cref="_gate"/> on the same thread (a CLR
    /// <c>Monitor</c> is reentrant), so no lock-free internal variants are
    /// needed. A <c>null</c> mutator result skips the Save — the transient-
    /// read-failure "don't clobber" path and the writer's dedup no-op both rely
    /// on this.</para></summary>
    public void Update(Func<HabituationProfileSnapshot?, HabituationProfileSnapshot?> mutate)
    {
        if (mutate is null) throw new ArgumentNullException(nameof(mutate));
        lock (_gate)
        {
            var next = mutate(Load());
            if (next is not null) Save(next);
        }
    }

    public void Save(HabituationProfileSnapshot snapshot)
    {
        lock (_gate)
        {
            GuardPath(); // H50: reject reparse-point / base-escape before writing

            // Stage 6c: WasRecoveredFromWipe is a one-shot flag set by the
            // loader on a synthesized snapshot. We do NOT persist it as true
            // — the next Save writes false (the default) so the on-disk file
            // doesn't pretend the wipe-recovery state is real. Mutating the
            // caller's snapshot here is intentional and documented: the flag
            // has served its purpose by the time Save runs.
            snapshot.WasRecoveredFromWipe = false;

            // AtomicJsonWriter handles tmp + File.Replace + first-write
            // fallback. Returns false on failure but we never surface that
            // to the caller — persistence must not crash the app, and the
            // next write attempt will retry.
            if (AtomicJsonWriter.TryWrite(_path, snapshot))
            {
                // H14: record that this profile has been written as v2 so a later
                // hand-edited schema_version:1 file is refused on load.
                TryWriteSchemaFloorFlag();
            }
        }
    }

    /// <summary>Canonical key for the per-path read-modify-write lock: the full path,
    /// case-insensitive (Windows). Falls back to the raw path if it cannot be
    /// canonicalised. All callers pass the same AppConfig path, so they share one lock.</summary>
    private static string GateKey(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>H50: guard against a reparse-pointed or out-of-base profile
    /// path. Expected base is the directory the profile (and its sibling
    /// schema-floor flag) live in — derived from <c>_path</c> via
    /// <see cref="Path.GetDirectoryName(string)"/>, no hardcoding. A legit file
    /// inside that dir (including a not-yet-created first-run profile) is
    /// untouched; only a symlink/junction or base escape throws
    /// <see cref="System.Security.SecurityException"/>.</summary>
    private void GuardPath()
    {
        string? baseDir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(baseDir))
            PathValidator.EnsureSafe(_path, baseDir);
    }

    private void TryWriteSchemaFloorFlag()
    {
        try
        {
            if (File.Exists(_schemaFloorFlagPath)) return;
            string? dir = Path.GetDirectoryName(_schemaFloorFlagPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_schemaFloorFlagPath, "v2");
        }
        catch { /* best-effort defense-in-depth guard */ }
    }
}
