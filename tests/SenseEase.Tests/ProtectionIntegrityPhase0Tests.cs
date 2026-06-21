using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 Phase-0 protection-integrity pins (docs/BETA_GATE_REVIEW_2026-06-14.md #5).
///
/// Vestibular-removal: the two original facts proved that vestibular personalization
/// (EffectiveTrigger 50) reached the VISIBLE protection earlier than the default (85). The
/// vestibular feature is retired (the app is now scoped to general comfort, not clinical
/// vestibular disorders) and <c>SetVestibularProtection</c> was deleted, so those facts are gone.
/// The NON-vestibular regression guard survives — re-expressed to actually EXERCISE the boot-restore
/// path the removal touched: a NON-vestibular persisted snapshot must (a) NOT trigger the
/// vestibular→Reactive migration and (b) drive a mask/FOV/tier trace byte-identical to a store-less
/// fresh engine at the same profile. A regression in the load/migration branch would diverge here.
/// </summary>
public class ProtectionIntegrityPhase0Tests
{
    private const double RampOfi = 22.0;

    private sealed class InMemoryProfileStore : IHabituationProfileStore
    {
        public HabituationProfileSnapshot? Stored;
        public HabituationProfileSnapshot? Load() => Stored;
        public void Save(HabituationProfileSnapshot snapshot) => Stored = snapshot;
    }

    [Fact]
    public void Beta1_5_NonVestibularSnapshot_LoadsUnperturbed_ByRemoval()
    {
        // A non-vestibular snapshot (the realistic, overwhelmingly-common shape) at the engine's
        // default profile. Loading it runs the real boot-restore — including the now-present
        // vestibular-migration branch, which must NOT fire (flag false).
        var store = new InMemoryProfileStore
        {
            Stored = new HabituationProfileSnapshot { Profile = "RESILIENT", VestibularProtectionEnabled = false },
        };
        var loaded = new MockInferenceEngine(profileStore: store);
        var fresh  = new MockInferenceEngine();   // store-less, field-default RESILIENT / trigger 85

        // The migration did NOT re-home this non-vestibular user (would be Reactive if it had).
        Assert.Equal(HabituationProfile.Resilient, loaded.HabituationProfile);

        // ...and the visible protection is byte-identical to the default engine, tick for tick.
        for (int i = 1; i <= 12; i++)
        {
            var sl = loaded.Update(RampOfi, "Passenger");
            var sf = fresh.Update(RampOfi, "Passenger");
            Assert.Equal(sf.MaskActive, sl.MaskActive);
            Assert.Equal(sf.FovRadiusPercent, sl.FovRadiusPercent);
            Assert.Equal(sf.VimsTier, sl.VimsTier);
        }
    }
}
