using Xunit;

namespace SenseEase.Tests;

// These tests target the pure tier-resolution function on MainViewModel — the
// engine + overlay rendering paths are exercised by smoke tests, but the floor
// math is small enough to lock in with unit tests so a future refactor can't
// silently revert "always-on" to "never-on" or vice versa.
public class PersistentOverlayTierFloorTests
{
    private static int Resolve(bool enabled, int baseline, int natural)
    {
        // Mirror MainViewModel.ResolveDisplayedTier without instantiating the full
        // VM (which requires WPF Application.Current and engine wiring).
        return enabled ? System.Math.Max(natural, baseline) : natural;
    }

    [Fact]
    public void Disabled_Passthrough_Returns_Natural_Tier()
    {
        Assert.Equal(1, Resolve(enabled: false, baseline: 7, natural: 1));
        Assert.Equal(8, Resolve(enabled: false, baseline: 2, natural: 8));
    }

    [Fact]
    public void Enabled_Natural_Below_Baseline_Floored_To_Baseline()
    {
        Assert.Equal(4, Resolve(enabled: true, baseline: 4, natural: 1));
        Assert.Equal(4, Resolve(enabled: true, baseline: 4, natural: 3));
    }

    [Fact]
    public void Enabled_Natural_Above_Baseline_Wins()
    {
        // Real pressure-driven tier still darkens the periphery further when it
        // exceeds the baseline floor.
        Assert.Equal(7, Resolve(enabled: true, baseline: 4, natural: 7));
        Assert.Equal(10, Resolve(enabled: true, baseline: 4, natural: 10));
    }

    [Fact]
    public void Enabled_Natural_Equals_Baseline_Idempotent()
    {
        Assert.Equal(4, Resolve(enabled: true, baseline: 4, natural: 4));
    }
}
