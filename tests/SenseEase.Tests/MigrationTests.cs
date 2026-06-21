using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Vestibular-removal migration: the vestibular-protection feature is retired (the app is now
/// scoped to general comfort, not clinical vestibular disorders). An existing user who persisted
/// <c>VestibularProtectionEnabled=true</c> (who used to boot into EffectiveTrigger=50 +
/// SevereStrikeLimitOverride=1) must NOT silently drop to the default trigger-85/Typical. The
/// engine's load path re-homes them into the most-cautious surviving profile (Reactive =
/// Cohort.Sensitive's no-net-relax floor + strike-limit-1 + the PassesProtectionFloorGuard
/// Reactive leg) and never re-arms vestibular. These tests pin that migration.
/// </summary>
public sealed class MigrationTests
{
    private sealed class InMemoryProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Stored;
        public HabituationProfileSnapshot? Load() => Stored;
        public void Save(HabituationProfileSnapshot snapshot) => Stored = snapshot;
    }

    [Fact]
    public void PersistedVestibularUser_SeedsReactiveProfile_OnLoad()
    {
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot
            {
                Profile = "TYPICAL",
                ProfileSource = "auto_profiling_default",
                PersonalPressureScale = 1.0,
                VestibularProtectionEnabled = true, // legacy on-disk vestibular flag
            },
        };

        var engine = new MockInferenceEngine(profileStore: store);

        // Re-homed into the most-cautious surviving profile.
        Assert.Equal(HabituationProfile.Reactive, engine.HabituationProfile);
    }

    [Fact]
    public void PersistedVestibularUser_DoesNotArmVestibular_NorPinTrigger50()
    {
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot
            {
                Profile = "TYPICAL",
                ProfileSource = "auto_profiling_default",
                PersonalPressureScale = 1.0,
                VestibularProtectionEnabled = true,
            },
        };

        var engine = new MockInferenceEngine(profileStore: store);

        // Vestibular can NEVER re-arm — the feature is retired.
        Assert.False(engine.IsVestibularProtectionEnabled);
        // And the old trigger-50 hard pin is gone: the migrated user sits at the engine default
        // (85), protected by the Reactive/Sensitive floors rather than the trigger-50 override.
        Assert.Equal(SenseDefaults.TriggerPressure, engine.HabituationEffectiveTrigger, 6);
        Assert.NotEqual(CalibrationProcessor.VestibularTriggerPressureOverride,
                        engine.HabituationEffectiveTrigger);
    }

    [Fact]
    public void NonVestibularUser_IsUnaffected_ByteIdenticalDefault()
    {
        // A snapshot that never had the flag is byte-identical to before: default trigger 85,
        // no profile re-home (the migration is a no-op for VestibularProtectionEnabled=false).
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot
            {
                Profile = "TYPICAL",
                ProfileSource = "default",
                PersonalPressureScale = 1.0,
                VestibularProtectionEnabled = false,
            },
        };

        var engine = new MockInferenceEngine(profileStore: store);

        Assert.False(engine.IsVestibularProtectionEnabled);
        Assert.Equal(SenseDefaults.TriggerPressure, engine.HabituationEffectiveTrigger, 6);
        // A fresh, store-less engine must behave identically (no silent drift).
        Assert.Equal(new MockInferenceEngine().HabituationEffectiveTrigger,
                     engine.HabituationEffectiveTrigger, 6);
    }

    [Fact]
    public void Migration_ClearsBoolAndAdvisory_OnFirstPersist()
    {
        // Both disk-durable vestibular signals must clear on the first post-migration persist:
        // the bool (BuildProfileSnapshotCore writes the now-false field) AND the advisory
        // (MergeCarryForward maps a carried MedicalAdvisory.Vestibular to null). Otherwise the
        // three chatbot override interceptors keep firing against a now-non-vestibular user.
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot
            {
                Profile = "TYPICAL",
                ProfileSource = "auto_profiling_default",
                PersonalPressureScale = 1.0,
                VestibularProtectionEnabled = true,
                MedicalAdvisoryShown = MedicalAdvisory.Vestibular,
            },
        };

        var engine = new MockInferenceEngine(profileStore: store);
        // The migration marks the snapshot dirty at boot; the first persist trigger flushes the
        // neutralized false/null over the on-disk true/Vestibular. Drive one Update() tick (which
        // calls MaybePersistProfileSnapshot at its tail) to force that first persist.
        engine.Update(0.0, "Passenger");
        engine.FlushPendingPersists();

        Assert.NotNull(store.Stored);
        Assert.False(store.Stored!.VestibularProtectionEnabled); // bool neutralized
        Assert.Null(store.Stored.MedicalAdvisoryShown);          // advisory neutralized
    }

    [Fact]
    public void Migration_ClearsStrikeOverride_FromAtypicalSnapshot()
    {
        // Defensive: a tampered/atypical snapshot carrying BOTH the vestibular flag AND a non-null
        // strike-override must NOT pin that stale cap on the migrated (now-non-vestibular) user — the
        // strike limit must come solely from the Reactive profile.
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot
            {
                Profile = "TYPICAL",
                ProfileSource = "auto_profiling_default",
                PersonalPressureScale = 1.0,
                VestibularProtectionEnabled = true,
                SevereStrikeLimitOverride = 5,
            },
        };

        var engine = new MockInferenceEngine(profileStore: store);
        engine.Update(0.0, "Passenger");
        engine.FlushPendingPersists();

        Assert.NotNull(store.Stored);
        Assert.Null(store.Stored!.SevereStrikeLimitOverride);   // cleared by the migration, not pinned
    }
}
