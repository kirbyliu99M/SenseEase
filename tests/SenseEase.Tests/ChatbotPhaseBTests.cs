using System;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Item 4 — the per-turn language pin must flip with the UI toggle (read live), regardless
/// of the language of any earlier context, and fall back to per-message Han detection when no
/// toggle is wired.</summary>
public class BuildLanguagePinTests
{
    [Fact]
    public void Pin_FollowsToggle_English()
        => WithProvider(() => true, () =>
            Assert.Contains("US English", MistralChatbotService.BuildLanguagePin("任意文字 mixed")));

    [Fact]
    public void Pin_FollowsToggle_TraditionalChinese()
        => WithProvider(() => false, () =>
            Assert.Contains("繁體中文", MistralChatbotService.BuildLanguagePin("any english text")));

    [Fact]
    public void Pin_NoProvider_FallsBackToHanDetection_Chinese()
        => WithProvider(null, () =>
            Assert.Contains("繁體中文", MistralChatbotService.BuildLanguagePin("我覺得頭暈")));

    [Fact]
    public void Pin_NoProvider_FallsBackToHanDetection_English()
        => WithProvider(null, () =>
            Assert.Contains("US English", MistralChatbotService.BuildLanguagePin("I feel dizzy")));

    [Fact]
    public void Pin_OverridesEarlierContextLanguage()
        => WithProvider(() => true, () =>
            // The override clause is what stops the [RecentAdjustments] echo from dragging the reply
            // back into the old language after a mid-session toggle.
            Assert.Contains("regardless of the language", MistralChatbotService.BuildLanguagePin("hi")));

    private static void WithProvider(Func<bool>? provider, Action body)
    {
        var prior = ChatbotUiLanguage.IsEnglishProvider;
        ChatbotUiLanguage.IsEnglishProvider = provider;
        try { body(); }
        finally { ChatbotUiLanguage.IsEnglishProvider = prior; }
    }
}

/// <summary>Item 5 — the repeated-discomfort throttle advises rest once the count reaches the
/// threshold within the rolling window, and the window slides as time passes.</summary>
public class DiscomfortSafetyGovernorTests
{
    [Fact]
    public void Advises_OnThirdReport_WithinWindow()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var gov = new DiscomfortSafetyGovernor(() => now) { Threshold = 3, Window = TimeSpan.FromMinutes(5) };

        Assert.False(gov.RecordAndShouldAdvise()); // 1
        now = now.AddMinutes(1);
        Assert.False(gov.RecordAndShouldAdvise()); // 2
        now = now.AddMinutes(1);
        Assert.True(gov.RecordAndShouldAdvise());  // 3 within 5 min → advise
    }

    [Fact]
    public void WindowSlides_OldEventsExpire()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var gov = new DiscomfortSafetyGovernor(() => now) { Threshold = 3, Window = TimeSpan.FromMinutes(5) };

        gov.RecordAndShouldAdvise();          // t=0
        now = now.AddMinutes(3);
        gov.RecordAndShouldAdvise();          // t=3
        now = now.AddMinutes(3);              // t=6 — the first event (t=0) is now outside the 5-min window
        Assert.False(gov.RecordAndShouldAdvise()); // only 2 in-window → no advice
        Assert.Equal(2, gov.CountInWindow());
    }

    [Fact]
    public void Reset_ClearsCount()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var gov = new DiscomfortSafetyGovernor(() => now) { Threshold = 2 };
        gov.RecordAndShouldAdvise();
        gov.Reset();
        Assert.False(gov.RecordAndShouldAdvise()); // back to 1 after reset
    }
}

/// <summary>Item 11 — the per-turn mood directive shifts the assistant's tone with the engine tier:
/// calm/light when low, soothing/reassuring when high.</summary>
public class BuildMoodDirectiveTests
{
    [Fact]
    public void LowTier_IsLightAndCalm()
        => Assert.Contains("calm", ChatbotViewModel.BuildMoodDirective(1));

    [Fact]
    public void MidTier_IsSupportive()
        => Assert.Contains("discomfort", ChatbotViewModel.BuildMoodDirective(5));

    [Fact]
    public void HighTier_IsReassuring()
        => Assert.Contains("strain", ChatbotViewModel.BuildMoodDirective(10));

    [Fact]
    public void Directive_ShiftsWithTier()
        => Assert.NotEqual(ChatbotViewModel.BuildMoodDirective(1), ChatbotViewModel.BuildMoodDirective(10));
}
