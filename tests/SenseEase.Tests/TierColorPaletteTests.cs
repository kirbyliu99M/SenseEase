using System.Windows.Media;
using System.Windows.Media.Imaging;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 3 — tests for the single source of truth on
/// tier → colour mapping. Bar and tray both consume this; tests pin the
/// invariants so a future "I'll just tweak the amber a bit" edit can't
/// silently desync the two surfaces.</summary>
public class TierColorPaletteTests
{
    // ── Band lookup ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5,  TierColorPalette.TierBand.Grey)]
    [InlineData(0,   TierColorPalette.TierBand.Grey)]
    [InlineData(1,   TierColorPalette.TierBand.Grey)]
    [InlineData(5,   TierColorPalette.TierBand.Grey)]
    [InlineData(6,   TierColorPalette.TierBand.Amber)]
    [InlineData(7,   TierColorPalette.TierBand.Amber)]
    [InlineData(8,   TierColorPalette.TierBand.Red)]
    [InlineData(10,  TierColorPalette.TierBand.Red)]
    [InlineData(11,  TierColorPalette.TierBand.Red)]   // defensive clamp
    [InlineData(999, TierColorPalette.TierBand.Red)]
    public void BandForTier_ReturnsExpectedBand(int tier, TierColorPalette.TierBand expected)
    {
        Assert.Equal(expected, TierColorPalette.BandForTier(tier));
    }

    // ── Brush (bar surface) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0x26, 0x66, 0x66, 0x66)] // grey, alpha 0.15
    [InlineData(5, 0x26, 0x66, 0x66, 0x66)]
    [InlineData(6, 0x4D, 0xE6, 0x8C, 0x28)] // amber, alpha 0.30
    [InlineData(7, 0x4D, 0xE6, 0x8C, 0x28)]
    [InlineData(8, 0x73, 0xDC, 0x32, 0x28)] // red,   alpha 0.45
    [InlineData(10, 0x73, 0xDC, 0x32, 0x28)]
    public void BrushForTier_MatchesSpecArgb(int tier, byte a, byte r, byte g, byte b)
    {
        var brush = TierColorPalette.BrushForTier(tier);
        Assert.Equal(a, brush.Color.A);
        Assert.Equal(r, brush.Color.R);
        Assert.Equal(g, brush.Color.G);
        Assert.Equal(b, brush.Color.B);
    }

    [Fact]
    public void BrushForTier_ReturnsFrozenSharedInstances()
    {
        // Same band → same frozen instance (no per-call allocation on the hot path).
        var grey1 = TierColorPalette.BrushForTier(1);
        var grey5 = TierColorPalette.BrushForTier(5);
        Assert.Same(grey1, grey5);
        Assert.True(grey1.IsFrozen);

        var amber6 = TierColorPalette.BrushForTier(6);
        var amber7 = TierColorPalette.BrushForTier(7);
        Assert.Same(amber6, amber7);

        var red8  = TierColorPalette.BrushForTier(8);
        var red10 = TierColorPalette.BrushForTier(10);
        Assert.Same(red8, red10);
    }

    // ── Icon (tray surface) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    public void IconForTier_Returns32x32FrozenBitmap(int tier)
    {
        var icon = TierColorPalette.IconForTier(tier);
        var bmp = Assert.IsType<RenderTargetBitmap>(icon);
        Assert.Equal(32, bmp.PixelWidth);
        Assert.Equal(32, bmp.PixelHeight);
        Assert.True(bmp.IsFrozen);
    }

    [Fact]
    public void IconForTier_ReturnsSameInstancePerBand()
    {
        // Two tiers in the same band must return the same cached bitmap —
        // tray re-render every state tick would allocate a 32×32 RTBitmap
        // otherwise, which is wasteful.
        Assert.Same(TierColorPalette.IconForTier(1),  TierColorPalette.IconForTier(5));
        Assert.Same(TierColorPalette.IconForTier(6),  TierColorPalette.IconForTier(7));
        Assert.Same(TierColorPalette.IconForTier(8),  TierColorPalette.IconForTier(10));
    }

    [Fact]
    public void IconForTier_DifferentBandsReturnDifferentInstances()
    {
        // Sanity: bands must be visually distinct cache entries.
        var grey  = TierColorPalette.IconForTier(1);
        var amber = TierColorPalette.IconForTier(6);
        var red   = TierColorPalette.IconForTier(10);
        Assert.NotSame(grey, amber);
        Assert.NotSame(amber, red);
        Assert.NotSame(grey, red);
    }
}
