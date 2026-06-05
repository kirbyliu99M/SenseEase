using System.Globalization;
using System.Windows.Data;
using SenseEase.App.Converters;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>D1 (M5) converter parity: the Home-hero converters must classify every
/// tier into the SAME comfort band, and that band must agree with the single source
/// <see cref="TierColorPalette.BandForTier"/> — so the ≤5 / 6-7 / 8-10 thresholds can
/// never drift between the colour bar and the hero copy.</summary>
public class TierConverterParityTests
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(1, "Steady")]
    [InlineData(5, "Steady")]
    [InlineData(6, "Rising")]
    [InlineData(7, "Rising")]
    [InlineData(8, "High")]
    [InlineData(10, "High")]
    public void ComfortBand_AgreesWithTierColorPalette(int tier, string expectedBand)
    {
        Assert.Equal(expectedBand, ComfortBands.For(tier));

        if (tier > 0)
        {
            string fromPalette = TierColorPalette.BandForTier(tier) switch
            {
                TierColorPalette.TierBand.Grey  => "Steady",
                TierColorPalette.TierBand.Amber => "Rising",
                TierColorPalette.TierBand.Red   => "High",
                _                               => "?",
            };
            Assert.Equal(fromPalette, ComfortBands.For(tier)); // single-source band
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void AllConverters_KeyOffTheSameBand(int tier)
    {
        var headline = new TierToHeadlineConverter();
        var subline  = new TierToSublineConverter();
        var label    = new TierToComfortLabelConverter();
        var band     = new TierToComfortBandConverter();

        Assert.Equal(ComfortBands.For(tier), (string)band.Convert(tier, typeof(string), null, Ci));
        Assert.False(string.IsNullOrWhiteSpace((string)headline.Convert(tier, typeof(string), null, Ci)));
        Assert.False(string.IsNullOrWhiteSpace((string)subline.Convert(tier, typeof(string), null, Ci)));
        Assert.False(string.IsNullOrWhiteSpace((string)label.Convert(tier, typeof(string), null, Ci)));
    }

    [Fact]
    public void Converters_AreOneWay_ConvertBackIsNoOp()
    {
        var c = new TierToHeadlineConverter();
        Assert.Equal(Binding.DoNothing, c.ConvertBack("x", typeof(int), null, Ci));
    }

    [Fact]
    public void DistinctBands_ProduceDistinctHeadlines()
    {
        var h = new TierToHeadlineConverter();
        string steady = (string)h.Convert(1, typeof(string), null, Ci);
        string rising = (string)h.Convert(6, typeof(string), null, Ci);
        string high   = (string)h.Convert(9, typeof(string), null, Ci);
        Assert.NotEqual(steady, rising);
        Assert.NotEqual(rising, high);
        Assert.NotEqual(steady, high);
    }
}
