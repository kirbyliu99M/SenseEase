using System.Globalization;
using System.Windows.Media;
using SenseEase.App.Converters;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UI-RESHAPE prep — the popup pulse + segment converters map the engine's 1–12 tier
/// (via TierColorPalette.BandForTier) onto the reference 3-state pulse and palette. Pure,
/// headless-safe (no Application). Verifies the band→{low/mid/high} boundaries, distinct
/// outputs per state, and null-safety.
/// </summary>
public class PopupStatusConverterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static SolidColorBrush Brush(int tier)
        => (SolidColorBrush)new TierToPopupStatusBrushConverter().Convert(tier, typeof(Brush), null, Inv);

    [Theory]
    // VeryHigh(L1-2) + High(L3-4) → low/calm (green)
    [InlineData(1, 0x22, 0xC5, 0x5E)]
    [InlineData(4, 0x22, 0xC5, 0x5E)]
    // Medium(L5-7) → mid (amber)
    [InlineData(5, 0xF5, 0x9E, 0x0B)]
    [InlineData(7, 0xF5, 0x9E, 0x0B)]
    // Low(L8-10) + VeryLow(L11-12) → high/dynamic (red)
    [InlineData(8, 0xEF, 0x44, 0x44)]
    [InlineData(12, 0xEF, 0x44, 0x44)]
    public void StatusBrush_MapsTierToReferencePalette(int tier, byte r, byte g, byte b)
    {
        var c = Brush(tier).Color;
        Assert.Equal(r, c.R);
        Assert.Equal(g, c.G);
        Assert.Equal(b, c.B);
    }

    [Fact]
    public void StatusWord_DistinctNonEmptyPerState()
    {
        var conv = new TierToPopupStatusWordConverter();
        var low = (string)conv.Convert(2, typeof(string), null, Inv);
        var mid = (string)conv.Convert(6, typeof(string), null, Inv);
        var high = (string)conv.Convert(10, typeof(string), null, Inv);

        Assert.False(string.IsNullOrWhiteSpace(low));
        Assert.False(string.IsNullOrWhiteSpace(mid));
        Assert.False(string.IsNullOrWhiteSpace(high));
        Assert.NotEqual(low, mid);
        Assert.NotEqual(mid, high);
    }

    [Fact]
    public void StatusGlyph_DistinctGeometryPerState()
    {
        var conv = new TierToPopupStatusGlyphConverter();
        var low = conv.Convert(2, typeof(Geometry), null, Inv) as Geometry;
        var high = conv.Convert(10, typeof(Geometry), null, Inv) as Geometry;

        Assert.NotNull(low);
        Assert.NotNull(high);
        Assert.NotSame(low, high);
    }

    [Theory]
    [InlineData(true, 0x02, 0x4A, 0xD8)]   // lit → Popup.SegOn (blue)
    public void SegmentFill_OnUsesAccent(bool on, byte r, byte g, byte b)
    {
        var brush = (SolidColorBrush)new SegmentFillConverter().Convert(on, typeof(Brush), null, Inv);
        Assert.Equal(r, brush.Color.R);
        Assert.Equal(g, brush.Color.G);
        Assert.Equal(b, brush.Color.B);
    }

    [Fact]
    public void SegmentFill_OffIsDimmed()
    {
        var brush = (SolidColorBrush)new SegmentFillConverter().Convert(false, typeof(Brush), null, Inv);
        Assert.True(brush.Color.A < 0x80); // translucent white, not the solid accent
    }

    [Fact]
    public void Converters_AreNullSafe()
    {
        Assert.NotNull(new TierToPopupStatusBrushConverter().Convert(null, typeof(Brush), null, Inv));
        Assert.NotNull(new TierToPopupStatusWordConverter().Convert(null, typeof(string), null, Inv));
        Assert.NotNull(new TierToPopupStatusGlyphConverter().Convert(null, typeof(Geometry), null, Inv));
        Assert.NotNull(new SegmentFillConverter().Convert(null, typeof(Brush), null, Inv));
    }
}
