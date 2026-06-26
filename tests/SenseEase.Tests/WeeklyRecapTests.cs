using System;
using SenseEase.App.ViewModels;
using SenseEase.Infrastructure.Reporting;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Weekly welcome recap (2026-06-27): the pure gate + bilingual fallback copy. The orchestration
/// (store read → LLM/fallback → chat bubble → prefs stamp) lives in ChatbotViewModel; here we pin the
/// once-per-ISO-week boundary logic (Monday-anchored, incl. the ISO-year rollover) + the empty-week skip + the
/// honest copy.</summary>
public class WeeklyRecapTests
{
    // ── IsDue: once per ISO week (ISO weeks start Monday) ─────────────────────────────────────────
    [Fact]
    public void IsDue_NeverShown_IsDue()
        => Assert.True(WeeklyRecap.IsDue(new DateTime(2026, 6, 24), lastShownLocal: null));

    [Fact]
    public void IsDue_SameIsoWeek_NotDue()
        // Mon 2026-06-22 and Wed 2026-06-24 are the same ISO week.
        => Assert.False(WeeklyRecap.IsDue(new DateTime(2026, 6, 24), new DateTime(2026, 6, 22)));

    [Fact]
    public void IsDue_NewIsoWeek_IsDue()
        // last shown Wed 2026-06-24; now Mon 2026-06-29 (next ISO week) → due.
        => Assert.True(WeeklyRecap.IsDue(new DateTime(2026, 6, 29), new DateTime(2026, 6, 24)));

    [Fact]
    public void IsDue_IsoYearRollover_SameWeek_NotDue()
        // Mon 2025-12-29 and Thu 2026-01-01 are both ISO 2026-W01 (the (year,week) key must agree).
        => Assert.False(WeeklyRecap.IsDue(new DateTime(2026, 1, 1), new DateTime(2025, 12, 29)));

    [Fact]
    public void IsDue_IsoYearRollover_WeekBoundary_IsDue()
        // Sun 2025-12-28 is ISO 2025-W52; Mon 2025-12-29 is ISO 2026-W01 → crosses the week boundary.
        => Assert.True(WeeklyRecap.IsDue(new DateTime(2025, 12, 29), new DateTime(2025, 12, 28)));

    // ── IsEmpty: skip a brand-new user's first week (no protections AND no condition) ─────────────
    [Theory]
    [InlineData(0, VimsCondition.Unknown, true)]   // genuinely empty
    [InlineData(5, VimsCondition.Unknown, false)]  // had protections
    [InlineData(0, VimsCondition.Stable, false)]   // calm week but a known condition
    [InlineData(3, VimsCondition.Moderate, false)]
    public void IsEmpty_Matrix(int times, VimsCondition c, bool expectedEmpty)
        => Assert.Equal(expectedEmpty, WeeklyRecap.IsEmpty(new HealthCardData(times, 0, c, Array.Empty<int>(), 7)));

    [Fact]
    public void IsEmpty_Null_IsEmpty() => Assert.True(WeeklyRecap.IsEmpty(null));

    // ── FallbackText: honest, count-correct, bilingual ───────────────────────────────────────────
    [Fact]
    public void Fallback_En_Singular()
    {
        string t = WeeklyRecap.FallbackText(1, VimsCondition.Stable, english: true);
        Assert.Contains("1 time", t);
        Assert.DoesNotContain("1 times", t);
        Assert.Contains("steady", t);
    }

    [Fact]
    public void Fallback_En_Plural()
    {
        string t = WeeklyRecap.FallbackText(4, VimsCondition.Moderate, english: true);
        Assert.Contains("4 times", t);
        Assert.Contains("sensitivity", t);
    }

    [Fact]
    public void Fallback_En_ZeroProtections_IsCalmNotZeroTimes()
    {
        string t = WeeklyRecap.FallbackText(0, VimsCondition.Stable, english: true);
        Assert.Contains("calm", t);
        Assert.DoesNotContain("0 time", t);   // never a sad "0 times" line
    }

    [Fact]
    public void Fallback_Zh_ContainsCountAndUnit()
    {
        string t = WeeklyRecap.FallbackText(3, VimsCondition.Stable, english: false);
        Assert.Contains("3 次", t);
        Assert.Contains("穩定", t);
    }

    [Fact]
    public void ConditionWord_MatchesHealthCardLabels()
    {
        Assert.Equal("Stable", WeeklyRecap.ConditionWord(VimsCondition.Stable, true));
        Assert.Equal("中等", WeeklyRecap.ConditionWord(VimsCondition.Moderate, false));
        Assert.Equal("明顯", WeeklyRecap.ConditionWord(VimsCondition.Severe, false));
    }

    [Fact]
    public void LlmUser_CarriesTheFacts()
    {
        string u = WeeklyRecap.LlmUser(7, VimsCondition.Moderate, english: true);
        Assert.Contains("7", u);
        Assert.Contains("Moderate", u);
    }
}
