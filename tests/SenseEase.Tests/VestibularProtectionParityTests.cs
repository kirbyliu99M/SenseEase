using System;
using System.IO;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// SAFETY-CRITICAL parity tests for the Alpha-2 addendum "cancel explicit
/// calibration → auto-profiling" vestibular path.
///
/// Once explicit calibration is cancelled, the calibration Q5 risk-factor branch
/// (Sensitive / trigger=50 / strikeLimit=1 / advisory=vestibular) no longer runs,
/// so <see cref="MockInferenceEngine.SetVestibularProtection"/> becomes the SOLE
/// source of the vestibular safety envelope. These tests PIN that the auto-profiling
/// path produces the SAME engine protection the cancelled calibration did
/// (EffectiveTrigger == <see cref="CalibrationProcessor.VestibularTriggerPressureOverride"/> = 50,
/// SevereStrikeLimitOverride == 1) AND that it survives a process restart via the
/// persisted snapshot field (restore-on-load).
///
/// State-contract tests: the engine is passive (Update is never driven) — every
/// assertion is on engine state set directly by SetVestibularProtection / the load
/// path, not on any pressure-integration behaviour.
/// </summary>
public class VestibularProtectionParityTests
{
    // ── 1. ON applies the SAME protection the cancelled calibration Q5 did ─────
    [Fact]
    public void SetVestibularProtection_on_applies_calibration_parity_envelope()
    {
        var engine = new MockInferenceEngine();

        engine.SetVestibularProtection(true);

        Assert.True(engine.IsVestibularProtectionEnabled);
        Assert.Equal(
            CalibrationProcessor.VestibularTriggerPressureOverride,
            engine.HabituationEffectiveTrigger);
        Assert.Equal(50, engine.HabituationEffectiveTrigger); // pin the literal too
        Assert.Equal(1, engine.HabituationSevereStrikeLimitOverride);
    }

    // ── 2. OFF (Settings "regret" toggle) reverts to engine defaults ──────────
    [Fact]
    public void SetVestibularProtection_off_reverts_to_engine_defaults()
    {
        var engine = new MockInferenceEngine();
        engine.SetVestibularProtection(true);

        engine.SetVestibularProtection(false);

        Assert.False(engine.IsVestibularProtectionEnabled);
        Assert.Equal(SenseDefaults.TriggerPressure, engine.HabituationEffectiveTrigger);
        Assert.Null(engine.HabituationSevereStrikeLimitOverride);
    }

    // ── 3. PERSIST + RESTORE across a (simulated) restart ─────────────────────
    // Engine A enables protection and persists the snapshot; a SECOND engine B
    // constructed over the SAME on-disk file must restore the FULL envelope at
    // load (this is the only vestibular source once calibration is cancelled, so
    // a missed restore is a silent safety regression).
    [Fact]
    public void VestibularProtection_survives_restart_via_snapshot_restore()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "senseease-vestibular-parity-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Engine A: fresh user, enables protection → persists the snapshot.
            var storeA = new FileHabituationProfileStore(path);
            var engineA = new MockInferenceEngine(profileStore: storeA);
            engineA.SetVestibularProtection(true);
            Assert.True(engineA.IsVestibularProtectionEnabled);

            // Engine B: fresh store + fresh engine over the SAME file (restart).
            var storeB = new FileHabituationProfileStore(path);
            var engineB = new MockInferenceEngine(profileStore: storeB);

            Assert.True(engineB.IsVestibularProtectionEnabled);
            Assert.Equal(
                CalibrationProcessor.VestibularTriggerPressureOverride,
                engineB.HabituationEffectiveTrigger);
            Assert.Equal(50, engineB.HabituationEffectiveTrigger);
            Assert.Equal(1, engineB.HabituationSevereStrikeLimitOverride);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            // The store also drops a sibling schema-floor flag next to the file.
            try { File.Delete(Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty, "schema_floor_v2.flag")); }
            catch { }
        }
    }

    // ── 4. Snapshot round-trips the persisted contract fields ─────────────────
    // After SetVestibularProtection(true) the on-disk snapshot must carry both
    // vestibular_protection_enabled == true AND medical_advisory_shown ==
    // Vestibular, so the chatbot's [habituation_state] block surfaces the advisory
    // for an auto-profiling (no-calibration) vestibular user.
    [Fact]
    public void SetVestibularProtection_on_round_trips_snapshot_fields()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "senseease-vestibular-parity-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileHabituationProfileStore(path);
            var engine = new MockInferenceEngine(profileStore: store);

            engine.SetVestibularProtection(true);

            HabituationProfileSnapshot? snapshot = store.Load();
            Assert.NotNull(snapshot);
            Assert.True(snapshot!.VestibularProtectionEnabled);
            Assert.Equal(MedicalAdvisory.Vestibular, snapshot.MedicalAdvisoryShown);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty, "schema_floor_v2.flag")); }
            catch { }
        }
    }
}
