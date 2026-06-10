using System.Globalization;
using SenseEase.App.Converters;
using SenseEase.App.Localization;
using Xunit;

namespace SenseEase.Tests;

/// <summary>UX-06 (D1 semantics): the hero pill's auto-mode token (Driver/Passenger) maps to
/// a calm plain-language protection label in BOTH languages, with locked polarity —
/// Driver = lighter protection, Passenger = full protection (never inverted, never implies
/// Driver gets MORE masking). Also pins the sibling Advanced-expander label converters
/// (mask-state, overlay-visibility) which exist because a <c>Setter.Value</c> cannot hold a
/// <c>{loc:T}</c> binding.</summary>
public class ModeToProtectionLabelConverterTests
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static object Convert(System.Windows.Data.IMultiValueConverter c, object mode, UILanguage lang)
        => c.Convert(new[] { mode, (object)lang }, typeof(string), null, Ci);

    // ── UX-06 hero pill: Mode → protection label ──────────────────────────────

    [Theory]
    [InlineData("Driver",    UILanguage.English,            "Active use · lighter protection")]
    [InlineData("Passenger", UILanguage.English,            "Watching · full protection")]
    [InlineData("Driver",    UILanguage.ChineseTraditional, "操作中（輕度防護）")]
    [InlineData("Passenger", UILanguage.ChineseTraditional, "觀看中（完整防護）")]
    public void Mode_MapsToPlainLabel_InBothLanguages(string mode, UILanguage lang, string expected)
    {
        var c = new ModeToProtectionLabelConverter();
        Assert.Equal(expected, (string)Convert(c, mode, lang));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mode_Polarity_DriverNeverImpliesMoreProtection(bool english)
    {
        // D1 invariant: Driver's label must read as LIGHTER, Passenger's as FULL — the
        // "full"/"完整" word must attach to Passenger, never to Driver.
        string driver    = ModeToProtectionLabelConverter.Localize("Driver", english);
        string passenger = ModeToProtectionLabelConverter.Localize("Passenger", english);

        Assert.NotEqual(driver, passenger);
        if (english)
        {
            Assert.Contains("lighter", driver);
            Assert.Contains("full", passenger);
            Assert.DoesNotContain("full", driver);
        }
        else
        {
            Assert.Contains("輕度", driver);
            Assert.Contains("完整", passenger);
            Assert.DoesNotContain("完整", driver);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-unexpected")]
    public void UnknownMode_FallsBackToFullProtection(string? mode)
    {
        // Conservative default for a motion-sensitivity product: unknown ⇒ full protection.
        Assert.Equal("Watching · full protection", ModeToProtectionLabelConverter.Localize(mode, english: true));
        Assert.Equal("觀看中（完整防護）", ModeToProtectionLabelConverter.Localize(mode, english: false));
    }

    // ── UX-06 Advanced expander: mask-state + overlay-visibility labels ────────

    [Theory]
    [InlineData(true,  UILanguage.English,            "Mask active")]
    [InlineData(false, UILanguage.English,            "Mask inactive")]
    [InlineData(true,  UILanguage.ChineseTraditional, "遮罩啟用中")]
    [InlineData(false, UILanguage.ChineseTraditional, "遮罩待命")]
    public void MaskState_MapsToLocalizedLabel(bool active, UILanguage lang, string expected)
    {
        var c = new MaskStateLabelConverter();
        Assert.Equal(expected, (string)Convert(c, active, lang));
    }

    [Theory]
    [InlineData(true,  UILanguage.English,            "Visible")]
    [InlineData(false, UILanguage.English,            "Hidden")]
    [InlineData(true,  UILanguage.ChineseTraditional, "顯示中")]
    [InlineData(false, UILanguage.ChineseTraditional, "隱藏")]
    public void OverlayVisibility_MapsToLocalizedLabel(bool visible, UILanguage lang, string expected)
    {
        var c = new OverlayVisibilityLabelConverter();
        Assert.Equal(expected, (string)Convert(c, visible, lang));
    }
}
