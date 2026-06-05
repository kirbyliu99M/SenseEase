using SenseEase.Core.Safety;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-1 Wave 1 (H3): vectors for the single shared self-harm safety floor.
/// Covers the gaps the two old per-service tables missed — Simplified Han,
/// full-width Latin (NFKC fold), and youth shorthand — plus the canonical
/// crisis responses and the redaction placeholder (H2).
/// </summary>
public class SafetyFloorTests
{
    [Theory]
    // Traditional (regression — must still trip)
    [InlineData("我想死了")]
    [InlineData("自殺")]
    [InlineData("我要跳樓")]
    [InlineData("不想活了，每天都好痛苦")]
    // Simplified (H3 — the old Ordinal table could not equate these code points)
    [InlineData("我想自杀")]
    [InlineData("有點想自残")]
    [InlineData("想结束生命")]
    [InlineData("打算跳楼")]
    [InlineData("我想死")]
    public void Trips_HanVariants_ReturnsTrueWithZhResponse(string text)
    {
        Assert.True(SafetyFloor.Trips(text, out string r));
        Assert.Equal(SafetyFloor.CrisisResponseZh, r);
        Assert.Contains("1925", r);
        Assert.Contains("findahelpline", r);
    }

    [Theory]
    [InlineData("I want to die")]
    [InlineData("I might kill myself")]
    // youth shorthand (H3)
    [InlineData("honestly kys")]
    [InlineData("kms tbh")]
    [InlineData("i want to unalive")]
    [InlineData("might off myself")]
    [InlineData("no reason to live anymore")]
    [InlineData("i'd be better off dead")]
    // full-width Latin folds to ASCII via NFKC (H3)
    [InlineData("ｉ ｗａｎｔ ｔｏ ｄｉｅ")]
    public void Trips_EnAndShorthandAndFullwidth_ReturnsTrueWithEnResponse(string text)
    {
        Assert.True(SafetyFloor.Trips(text, out string r));
        Assert.Equal(SafetyFloor.CrisisResponseEn, r);
        Assert.Contains("988", r);
        Assert.Contains("findahelpline", r);
    }

    [Theory]
    [InlineData("I have a headache")]
    [InlineData("我有點頭暈")]
    [InlineData("the screen is killing me")]   // idiom, not in the phrase list
    [InlineData("好多了，謝謝")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Trips_NonSafetyText_ReturnsFalse(string? text)
    {
        Assert.False(SafetyFloor.Trips(text, out string r));
        Assert.Empty(r);
    }

    [Fact]
    public void Trips_EnPhraseInHanContext_UsesZhResponse()
    {
        // Mixed input: an English phrase trips the en loop, but IsHan(userText)
        // is true so the localized response is the zh crisis text.
        Assert.True(SafetyFloor.Trips("今天真的 want to die 了", out string r));
        Assert.Equal(SafetyFloor.CrisisResponseZh, r);
        Assert.DoesNotContain("988", r);
    }

    [Fact]
    public void RedactedPlaceholder_IsTheNonPiiToken()
    {
        Assert.Equal("[REDACTED: safety_floor]", SafetyFloor.RedactedPlaceholder);
    }

    [Fact]
    public void CrisisResponseZh_UsesCanonicalFullWidthColon()
    {
        // C3: the canonical punctuation is the full-width colon. Guards against
        // the half-width drift that previously diverged the two services.
        Assert.Contains("協助：", SafetyFloor.CrisisResponseZh);
        Assert.DoesNotContain("協助:台灣", SafetyFloor.CrisisResponseZh);
    }
}
