using System;
using System.Threading;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// overlay-bar-redesign D1-D4 contract tests for the redesigned
/// NotificationCardWindow. WPF window construction requires an STA thread,
/// so each test spins up its own STA thread + Dispatcher loop and pumps an
/// arrange/act/assert closure onto it. Pattern lifted from MainWindowSmokeTests.
///
/// <para>Capture exceptions back to the test thread via a captured
/// <see cref="Exception"/> field so xUnit gets the original failure stack
/// instead of a generic "thread died" error.</para>
/// </summary>
public class NotificationCardWindowTests
{
    /// <summary>Test 1 (D2): SetContent formats the numeric title with
    /// rounded pressure. Updated UX iter 3: tier shows "N/10" (10-band
    /// VIMS scale is unambiguous now) and pressure shows raw value with
    /// the mask-trigger reference (no fake "/100" denominator since
    /// LeakyIntegrator is unbounded).</summary>
    [Fact]
    public void NotificationCardWindow_SetContent_FormatsNumericTitle()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            // Force template instantiation so TitleText is non-null.
            w.ApplyTemplate();
            w.SetContent(TierColorPalette.TierBand.Amber, 3, 27.4);
            // v0.9.2: title localised; pressure shows as raw integer
            // (LeakyIntegrator is unbounded — no fake "/100" denominator).
            Assert.Equal("VIMS 視覺壓力　第 3 級 / 10　·　壓力指數 27", w.TitleText.Text);
        });
    }

    /// <summary>Test 2 (D3): IsPowerSaveActive overrides the numeric title
    /// with the bilingual "sensing paused" message; subsequent SetContent
    /// calls don't reveal the tier/pressure until the override clears.</summary>
    [Fact]
    public void NotificationCardWindow_SetContent_PowerSaveOverride()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            w.ApplyTemplate();
            w.IsPowerSaveActive = true;
            w.SetContent(TierColorPalette.TierBand.Red, 8, 89);
            // v0.9.2: zh-only below the title's VIMS product term.
            Assert.Equal("VIMS · 省電模式中", w.TitleText.Text);
            Assert.Equal("感測已暫停，可點擊「省電」恢復。", w.BodyText.Text);
        });
    }

    /// <summary>Test 3 (D3): toggling the in-card CheckBox programmatically
    /// raises PowerSaveToggled with the new boolean value.</summary>
    [Fact]
    public void NotificationCardWindow_PowerSaveToggle_RaisesEvent()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            w.ApplyTemplate();
            bool? received = null;
            w.PowerSaveToggled += (_, on) => received = on;

            w.PowerSaveToggle.IsChecked = true;
            Assert.True(received);

            w.PowerSaveToggle.IsChecked = false;
            Assert.False(received);
        });
    }

    /// <summary>Helper: runs <paramref name="body"/> on a fresh STA thread
    /// and re-throws any exception on the calling thread so xUnit reports
    /// the actual assertion failure instead of a generic timeout.</summary>
    private static void RunOnSta(Action body)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "STA test timed out");
        if (captured != null)
            throw new Xunit.Sdk.XunitException("STA test threw: " + captured);
    }
}
