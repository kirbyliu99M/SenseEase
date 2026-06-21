using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Health-report redesign §8 (P3): the runtime medical-overreach wall
/// <see cref="ReportClaimGuard.ContainsBannedClaim"/>. Pins the load-bearing guarantees: it catches
/// inflected EN claims at the stem level, catches the ZH claim terms, runs BOTH language lists on EVERY
/// input regardless of script (build-gate 4), and lets neutral number-restating prose through.</summary>
public class ReportClaimGuardTests
{
    [Theory]
    [InlineData("Your tolerance has clearly improved.")]      // toler + improve
    [InlineData("You are recovering and adapting well.")]     // recover + adapt
    [InlineData("The desensitization seems to be working.")]  // desensiti
    [InlineData("You're getting used to the motion now.")]    // getting used to
    [InlineData("You should feel better very soon.")]         // feel better
    [InlineData("Your symptoms are fewer this week.")]        // symptom
    [InlineData("This is real neuroplastic rehab progress.")] // neuroplastic + rehab
    [InlineData("Over 30 minutes of protection each day.")]   // minutes-of regex
    public void Catches_EnglishClaims(string prose)
        => Assert.True(ReportClaimGuard.ContainsBannedClaim(prose));

    [Theory]
    [InlineData("You're making great progress this month.")]   // progress — was an EN gap (R2)
    [InlineData("This is getting easier for you.")]             // easier
    [InlineData("Your comfort is clearly better now.")]         // better (not only the phrase "feel better")
    [InlineData("You seem more resilient to the motion lately.")] // resilien (word-boundary)
    [InlineData("你的狀況正在好轉。")]                            // 好轉 — ZH symmetry twin
    public void Catches_ParaphrasedImprovementClaims(string prose)
        => Assert.True(ReportClaimGuard.ContainsBannedClaim(prose));

    [Theory]
    [InlineData("The display adapter was reset to its defaults.")]   // 'adapter' must NOT trip 'adapt' (R7)
    [InlineData("An adaptive layout filled the available screen.")]  // 'adaptive' must NOT trip 'adapt' (R7)
    public void DoesNotFalseFire_OnBenignAdapterWords(string prose)
        => Assert.False(ReportClaimGuard.ContainsBannedClaim(prose));

    [Theory]
    [InlineData("你的耐受度明顯提升。")]   // 耐受
    [InlineData("不適已經改善很多。")]      // 改善
    [InlineData("你已經適應並且習慣了。")] // 適應 + 習慣了
    [InlineData("整體症狀減少了。")]        // 症狀 + 減少
    [InlineData("這是真正的康復與進步。")] // 康復 + 進步
    public void Catches_ChineseClaims(string prose)
        => Assert.True(ReportClaimGuard.ContainsBannedClaim(prose));

    [Theory]
    [InlineData("Comfort check-ins averaged 6.2 across 14 days.")]
    [InlineData("With protection on the mean was 5; with it off, 9.")]
    [InlineData("The current protection strength is balanced.")]
    [InlineData("舒適度回報採用 0–20 量表，整體平均為 6.2。")]
    [InlineData("開啟防護時平均為 5，關閉時為 9。")]
    [InlineData("")]
    [InlineData(null)]
    public void Passes_NeutralOrEmpty(string? prose)
        => Assert.False(ReportClaimGuard.ContainsBannedClaim(prose));

    [Fact]
    public void RunsBothLanguageLists_RegardlessOfScript()
    {
        // An English-pinned section that smuggles a ZH claim is still caught …
        Assert.True(ReportClaimGuard.ContainsBannedClaim("Everything looks fine. 症狀 are fewer."));
        // … and a ZH-pinned section that smuggles an EN claim is too.
        Assert.True(ReportClaimGuard.ContainsBannedClaim("一切正常，with great recovery this month."));
    }
}
