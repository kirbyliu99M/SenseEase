using System;
using System.IO;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Alpha-2 audit fix: a persisted <c>personal_pressure_scale</c> must re-DERIVE the
/// engine's <c>EffectiveTrigger</c> on load. Before the fix the snapshot field round-tripped
/// but the engine booted at the DEFAULT trigger — silently dropping the user's earned
/// sensitivity until their next chatbot turn. These tests pin the load-path re-derivation
/// (same arithmetic as the runtime calibration / chatbot paths: effective = default / scale),
/// and that scale==1.0 leaves the engine default untouched.</summary>
public class PersonalPressureScalePersistenceTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "senseease-pps-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "schema_floor_v2.flag")); }
        catch { }
    }

    [Fact]
    public void PersistedPressureScale_ReDerivesEffectiveTrigger_OnLoad()
    {
        string path = TempPath();
        try
        {
            // Persist a NON-default scale (1.5 = more sensitive) as a chatbot
            // AdjustSensitivity would. No calibration (auto-profiling world), not vestibular.
            new FileHabituationProfileStore(path).Save(new HabituationProfileSnapshot
            {
                UpdatedAtIso          = "2026-06-01T08:00:00.0000000Z",
                Profile               = "TYPICAL",
                ProfileSource         = "chatbot_adjust_sensitivity",
                PersonalPressureScale = 1.5,
                CalibrationCount      = 0,
                VestibularProtectionEnabled = false,
            });

            // Fresh engine over the same file (simulated restart).
            var engine = new MockInferenceEngine(profileStore: new FileHabituationProfileStore(path));

            double expected = SenseDefaults.TriggerPressure / 1.5;
            Assert.Equal(expected, engine.HabituationEffectiveTrigger, 3);
            Assert.NotEqual(SenseDefaults.TriggerPressure, engine.HabituationEffectiveTrigger);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DefaultPressureScale_LeavesEngineDefaultTrigger_OnLoad()
    {
        string path = TempPath();
        try
        {
            new FileHabituationProfileStore(path).Save(new HabituationProfileSnapshot
            {
                UpdatedAtIso          = "2026-06-01T08:00:00.0000000Z",
                Profile               = "TYPICAL",
                ProfileSource         = "default",
                PersonalPressureScale = 1.0,
                VestibularProtectionEnabled = false,
            });

            var engine = new MockInferenceEngine(profileStore: new FileHabituationProfileStore(path));

            Assert.Equal(SenseDefaults.TriggerPressure, engine.HabituationEffectiveTrigger);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void VestibularOverride_WinsOverPersistedScale_OnLoad()
    {
        string path = TempPath();
        try
        {
            // A vestibular user with ALSO a non-default scale: the TriggerPressure-50
            // hard-override must win (the vestibular branch runs after the scale re-derive).
            new FileHabituationProfileStore(path).Save(new HabituationProfileSnapshot
            {
                UpdatedAtIso          = "2026-06-01T08:00:00.0000000Z",
                Profile               = "SENSITIVE",
                ProfileSource         = "chatbot_adjust_sensitivity",
                PersonalPressureScale = 1.5,
                VestibularProtectionEnabled = true,
            });

            var engine = new MockInferenceEngine(profileStore: new FileHabituationProfileStore(path));

            Assert.Equal(CalibrationProcessor.VestibularTriggerPressureOverride,
                         engine.HabituationEffectiveTrigger);
        }
        finally { Cleanup(path); }
    }
}
