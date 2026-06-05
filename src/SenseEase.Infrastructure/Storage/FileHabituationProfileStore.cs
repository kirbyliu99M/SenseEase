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
    private readonly object _gate = new();
    private const int SupportedSchemaVersion = 2;
    private const int LegacyV1SchemaVersion = 1;

    // H14: schema-downgrade guard. Editing the file to {"schema_version":1,...}
    // null-fills MedicalAdvisoryShown and bypasses the vestibular safety
    // intercept. Once this profile has been written as v2 we drop a sibling flag;
    // its presence (or a v1 file whose updated_at_iso is at/after the v2 ship date)
    // marks a v1 load as a downgrade, which we refuse — falling back to RESILIENT
    // defaults is safer than a null-filled advisory.
    private readonly string _schemaFloorFlagPath;
    private static readonly DateTime V2SchemaShipDateUtc =
        new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Constructor with optional wipe-probe artifact path. When the
    /// profile JSON is missing but the artifact at this path exists, Load
    /// returns a wipe-recovery snapshot instead of null. Pass
    /// <c>AppConfig.HabituationStatePath</c> to enable the wipe-detection
    /// path; pass <c>null</c> (default) to disable it (e.g. in tests that
    /// don't exercise the wipe scenario).</summary>
    public FileHabituationProfileStore(string path, string? wipeProbeArtifactPath = null)
    {
        _path = path;
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
                    if (IsSchemaDowngrade(snap))
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
        snap.SchemaVersion = SupportedSchemaVersion;
        // Defensive: explicit null-fill in case a future v2-field property
        // initialiser changes and a v1 deserialisation path leaves something
        // in an unexpected state. CLR defaults already cover this but the
        // explicit assignment documents the contract.
        snap.LongitudinalObservations ??= new();
        return snap;
    }

    /// <summary>H14: a v1 file is a downgrade attack if this install has already
    /// written a v2 file (flag present) OR the v1 file claims an updated_at_iso at
    /// or after the v2 schema ship date (no genuine v1 file is written after v2
    /// started shipping).</summary>
    private bool IsSchemaDowngrade(HabituationProfileSnapshot v1Snap)
    {
        try { if (File.Exists(_schemaFloorFlagPath)) return true; } catch { }
        if (DateTime.TryParse(v1Snap.UpdatedAtIso,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime updated)
            && updated.ToUniversalTime() >= V2SchemaShipDateUtc)
            return true;
        return false;
    }

    public void Save(HabituationProfileSnapshot snapshot)
    {
        lock (_gate)
        {
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
