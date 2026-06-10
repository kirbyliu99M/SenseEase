using System;
using SenseEase.App.Localization;
using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Wave B1b UX-03 / INTENT-05 / UX-04 — tray liveness honesty + localization.
///
/// <para>The full <see cref="TrayIconService"/> can't be constructed on xUnit's
/// default (non-STA) scheduler — it builds a WPF <c>TaskbarIcon</c> + <c>ContextMenu</c>
/// + <c>DispatcherTimer</c> (the same reason
/// <see cref="TierColorPaletteTests"/>'s full-lifecycle test is <c>[Skip]</c>ed).
/// So these tests exercise the WPF-free internal seams the service now exposes via
/// <c>InternalsVisibleTo("SenseEase.Tests")</c> (already wired in
/// SenseEase.App.csproj): the copy generators and the INTENT-05 clobber-guard
/// predicate that <see cref="TrayIconService"/>'s live methods delegate to.</para>
///
/// <para>What is pinned:</para>
/// <list type="bullet">
///   <item>UX-03: Paused and Faulted copy can never be confused — the fault copy
///         is the honest automatic-recovery verdict (no "paused"/暫停, no fake "tap
///         to retry"), and carries a non-colour-only differentiator (a glyph
///         prefix), while "paused"/已暫停 stays EXCLUSIVE to the user power-save
///         path.</item>
///   <item>INTENT-05: while a fault is showing, the per-tick label refresh must
///         preserve the fault verdict (refresh the stash, not the visible
///         header/tooltip) — pinned through the pure
///         <c>ShouldPreserveFaultVerdict</c> predicate the live
///         <c>RefreshLabels</c> branches on.</item>
///   <item>UX-04: the formerly-hardcoded-zh menu strings (Settings, Quit,
///         power-save status) differ between languages, so a live EN / 繁中 flip
///         actually re-picks them.</item>
/// </list></summary>
public class TrayFaultCopyTests
{
    // ── UX-03: fault copy is honest + paused-free, both languages ─────────────

    [Theory]
    [InlineData(false, "偵測中斷，正在自動重新連線")] // zh per spec
    [InlineData(true,  "Sensing interrupted — reconnecting automatically")] // en per spec
    public void FaultStatusText_UsesHonestReconnectingCopy(bool isEnglish, string expectedCore)
    {
        string s = TrayIconService.FaultStatusText(isEnglish);
        Assert.Contains(expectedCore, s);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultStatusText_NeverSaysPausedOrTapToRetry(bool isEnglish)
    {
        string s = TrayIconService.FaultStatusText(isEnglish);
        // UX-03: "paused"/暫停 wording is EXCLUSIVE to deliberate user pause; the
        // fault verdict must not borrow it, or the two states become confusable.
        Assert.DoesNotContain("paused", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("暫停", s);
        // The old copy offered a "tap to retry" that retried nothing — recovery is
        // automatic via WgcSensingProvider, so the retry affordance must be gone.
        Assert.DoesNotContain("tap to retry", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("重試", s);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultToolTipText_NeverSaysPausedOrTapToRetry(bool isEnglish)
    {
        string s = TrayIconService.FaultToolTipText(isEnglish);
        Assert.DoesNotContain("paused", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("暫停", s);
        Assert.DoesNotContain("tap to retry", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("重試", s);
        // Tooltip keeps the brand prefix so it reads consistently with the healthy
        // "SenseEase — …" tooltip shape.
        Assert.StartsWith("SenseEase — ", s);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultCopy_CarriesNonColourOnlyDifferentiator(bool isEnglish)
    {
        // UX-03 hard rule 6: status must never be conveyed by colour alone. The
        // indigo tile never recolours, so the fault read is a glyph + words. Both
        // the status item and the tooltip must carry the warning glyph PREFIX.
        Assert.Contains(TrayIconService.FaultGlyph, TrayIconService.FaultStatusText(isEnglish));
        Assert.Contains(TrayIconService.FaultGlyph, TrayIconService.FaultToolTipText(isEnglish));
        Assert.StartsWith(TrayIconService.FaultGlyph, TrayIconService.FaultStatusText(isEnglish));
    }

    [Fact]
    public void FaultCopy_IsLanguageDistinct()
    {
        // A live EN / 繁中 flip must actually change the visible fault copy.
        Assert.NotEqual(TrayIconService.FaultStatusText(false), TrayIconService.FaultStatusText(true));
        Assert.NotEqual(TrayIconService.FaultToolTipText(false), TrayIconService.FaultToolTipText(true));
    }

    // ── UX-03: power-save ("paused") copy stays exclusive + honest ────────────

    [Theory]
    [InlineData(false, "已暫停 · 點此恢復")] // zh power-save status (spec UX-04 string)
    [InlineData(true,  "Paused by you — tap to resume")]
    public void PowerSaveStatusText_IsTheDeliberatePauseCopy(bool isEnglish, string expected)
    {
        Assert.Equal(expected, TrayIconService.PowerSaveStatusText(isEnglish));
    }

    [Fact]
    public void PowerSaveCopy_OwnsThePausedWording_FaultDoesNot()
    {
        // The "paused"/已暫停 vocabulary belongs to the power-save path ONLY; the
        // fault path must not collide with it. Pin both halves so a future copy
        // edit can't reintroduce the UX-03 confusion.
        Assert.Contains("暫停", TrayIconService.PowerSaveStatusText(false));
        Assert.Contains("Paused", TrayIconService.PowerSaveStatusText(true));
        Assert.DoesNotContain("暫停", TrayIconService.FaultStatusText(false));
        Assert.DoesNotContain("paused", TrayIconService.FaultStatusText(true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerSaveStatusText_IsLanguageDistinct()
    {
        Assert.NotEqual(
            TrayIconService.PowerSaveStatusText(false),
            TrayIconService.PowerSaveStatusText(true));
    }

    // ── INTENT-05: per-tick refresh preserves the fault verdict ───────────────

    [Theory]
    [InlineData(true,  true)]   // fault stash active  → preserve (refresh stash, skip visible writes)
    [InlineData(false, false)]  // no fault            → normal write path runs
    public void ShouldPreserveFaultVerdict_GuardsOnlyWhenFaultStashActive(bool stashActive, bool expected)
    {
        // This predicate is the SAME branch the live RefreshLabels takes (it calls
        // ShouldPreserveFaultVerdict(_preFaultStatusText != null)). When true, the
        // visible header/tooltip are left untouched so the fault verdict survives
        // the ~0.6 s self-heal warmup ticks instead of being clobbered back to a
        // healthy band label.
        Assert.Equal(expected, TrayIconService.ShouldPreserveFaultVerdict(stashActive));
    }

    // ── UX-04: the formerly-zh-only menu strings re-pick on a language flip ───
    //
    // The menu items themselves are WPF MenuItems we can't build off-STA, but the
    // re-pick is a LocalizationManager.Pick over the exact string pairs the live
    // RefreshLanguageDependentLabels uses. Asserting the picked value tracks the
    // singleton's language proves the flip changes the header (the strings are
    // language-distinct, so a no-op pick would fail).

    [Fact]
    public void SettingsAndQuit_StringsArePickedPerLanguage()
    {
        var loc = LocalizationManager.Current;
        var saved = loc.Language;
        try
        {
            loc.Language = UILanguage.ChineseTraditional;
            Assert.Equal("設定", loc.Pick("設定", "Settings"));
            Assert.Equal("退出 SenseEase", loc.Pick("退出 SenseEase", "Quit SenseEase"));

            loc.Language = UILanguage.English;
            Assert.Equal("Settings", loc.Pick("設定", "Settings"));
            Assert.Equal("Quit SenseEase", loc.Pick("退出 SenseEase", "Quit SenseEase"));
        }
        finally
        {
            loc.Language = saved;
        }
    }

    [Fact]
    public void PowerSaveStatus_FollowsTheLanguageSingleton()
    {
        // UX-04: the power-save header (formerly zh-only) now tracks the toggle.
        var loc = LocalizationManager.Current;
        var saved = loc.Language;
        try
        {
            loc.Language = UILanguage.ChineseTraditional;
            Assert.Equal("已暫停 · 點此恢復", TrayIconService.PowerSaveStatusText(loc.IsEnglish));

            loc.Language = UILanguage.English;
            Assert.Equal("Paused by you — tap to resume", TrayIconService.PowerSaveStatusText(loc.IsEnglish));
        }
        finally
        {
            loc.Language = saved;
        }
    }
}
