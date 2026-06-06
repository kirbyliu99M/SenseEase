using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>D6 (master plan M7) day-1 protection exit-gate. The calm field now
/// defaults ON at baseline tier 1 — "ready", not "always dark": tier 1 is 0%
/// alpha, so nothing visibly dims at rest, yet a real motion spike (higher
/// natural tier) still wins. Both flips must land together (risk R-B: a tier-4
/// baseline would over-dim every new user ~43%).
///
/// <para>The full VM needs <c>Application.Current</c> + engine wiring to
/// construct, so — like <c>PersistentOverlayTierFloorTests</c> — we lock the
/// floor math directly here; the production field defaults
/// (<c>PersistentOverlayEnabled=true</c>, <c>PersistentOverlayBaselineTier=1</c>)
/// are exercised at construction by <c>MainWindowSmokeTests</c>.</para></summary>
public class D6DayOneProtectionTests
{
    // Mirror of MainViewModel.ResolveDisplayedTier.
    private static int Resolve(bool enabled, int baseline, int natural) =>
        enabled ? System.Math.Max(natural, baseline) : natural;

    [Fact]
    public void BaselineTier1_FloorsToOne_AndIsZeroAlphaAtRest()
    {
        // Enabled at baseline 1 with a calm screen → displayed tier 1.
        Assert.Equal(1, Resolve(enabled: true, baseline: 1, natural: 1));
        // L1 (VimsLevels[0]) must be 0% alpha — nothing visibly dims at rest.
        Assert.Equal(0.00, OverlayWindow.VimsLevels[0].Alpha, 3);
    }

    [Fact]
    public void NaturalSpike_StillWins_OverBaseline1()
    {
        // A real pressure-driven tier still darkens the periphery above the floor.
        Assert.Equal(5, Resolve(enabled: true, baseline: 1, natural: 5));
        Assert.Equal(10, Resolve(enabled: true, baseline: 1, natural: 10));
    }

    [Fact]
    public void Disabled_StillPassesThrough()
    {
        // Sanity: turning the field off is still a clean pass-through.
        Assert.Equal(3, Resolve(enabled: false, baseline: 1, natural: 3));
    }
}
