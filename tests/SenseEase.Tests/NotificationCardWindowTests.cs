using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Inference;
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
    /// rounded pressure. Level shows "N/12" (native 12-level scale — Adaptive
    /// Fit Q2=B; was the 10-band scale) and pressure shows raw value with
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
            w.SetContent(TierColorPalette.TierBand.Medium, 3, 27.4);
            // BC-1 (D2): the title is now plain-language app-action keyed off the
            // band; the raw tier/pressure is demoted to a mono subline. 5級 remap:
            // old Amber → Medium, which collapses to the same "正在緩和邊緣" copy.
            Assert.Equal("正在緩和邊緣", w.TitleText.Text);
            Assert.Equal("第 3 級 / 12　·　壓力 27", w.TelemetryText.Text);
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
            w.SetContent(TierColorPalette.TierBand.VeryLow, 8, 89);
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

    // ── D3 (audit) — M3 Phase 7e chatbot proposal section ────────────────

    /// <summary>D3 Test A: setting ActiveProposal to a non-null value reveals
    /// the proposal panel and populates summary/rationale; setting it back to
    /// null collapses the panel. Closes the audit-flagged D3 gap (plan promised
    /// a Card unit test for the proposal section but none existed in v0.9).</summary>
    [Fact]
    public void NotificationCardWindow_ActiveProposal_TogglesPanelAndPopulatesText()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            w.ApplyTemplate();
            Assert.Equal(Visibility.Collapsed, w.ProposalPanel.Visibility);

            var proposal = new ProfileOverrideProposal(
                Intent:        OverrideIntent.SwitchProfile,
                TargetField:   "profile",
                ProposedValue: "SENSITIVE",
                Rationale:     "user reports easy nausea",
                ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));

            w.ActiveProposal = proposal;

            Assert.Equal(Visibility.Visible, w.ProposalPanel.Visibility);
            Assert.Contains("SENSITIVE", w.ProposalSummaryText.Text);
            Assert.Contains("profile", w.ProposalSummaryText.Text);
            Assert.Contains("user reports easy nausea", w.ProposalRationaleText.Text);
            Assert.True(w.ProposalAcceptBtn.IsEnabled);

            w.ActiveProposal = null;
            Assert.Equal(Visibility.Collapsed, w.ProposalPanel.Visibility);
        });
    }

    /// <summary>D3 Test B: clicking Accept raises <see cref="NotificationCardWindow.ProposalAccepted"/>
    /// with the active proposal payload. Mirrors how MainViewModel wires the
    /// event to <see cref="HabituationOverrideApplicator.ApplyAccepted"/>.</summary>
    [Fact]
    public void NotificationCardWindow_AcceptClick_RaisesProposalAcceptedWithPayload()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            w.ApplyTemplate();
            var proposal = new ProfileOverrideProposal(
                Intent:        OverrideIntent.AdjustStrikeLimit,
                TargetField:   "strike_limit",
                ProposedValue: 4,
                Rationale:     "user wants stricter cutoff",
                ExpiresAtUtc:  DateTime.UtcNow.AddHours(6));
            w.ActiveProposal = proposal;

            ProfileOverrideProposal? received = null;
            w.ProposalAccepted += (_, p) => received = p;

            w.ProposalAcceptBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.NotNull(received);
            Assert.Equal(OverrideIntent.AdjustStrikeLimit, received!.Intent);
            Assert.Equal(4, received.ProposedValue);
            Assert.False(w.ProposalAcceptBtn.IsEnabled);
        });
    }

    /// <summary>D3 Test C: clicking Decline raises <see cref="NotificationCardWindow.ProposalDeclined"/>
    /// and immediately clears the panel (so the user doesn't re-click).</summary>
    [Fact]
    public void NotificationCardWindow_DeclineClick_RaisesProposalDeclinedAndClearsPanel()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            w.ApplyTemplate();
            var proposal = new ProfileOverrideProposal(
                Intent:        OverrideIntent.AdjustSensitivity,
                TargetField:   "sensitivity",
                ProposedValue: 0.6,
                Rationale:     "test",
                ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));
            w.ActiveProposal = proposal;

            ProfileOverrideProposal? received = null;
            w.ProposalDeclined += (_, p) => received = p;

            w.ProposalDeclineBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.NotNull(received);
            Assert.Equal(OverrideIntent.AdjustSensitivity, received!.Intent);
            Assert.Equal(Visibility.Collapsed, w.ProposalPanel.Visibility);
        });
    }

    /// <summary>D3 Test D: <see cref="NotificationCardWindow.ShowProposalApplyResult"/>
    /// with a BlockedBySafety result keeps the panel visible, surfaces the
    /// intercept reason in red, and disables Accept. This is the surface
    /// MainViewModel uses to feed Phase 7f intercepts back to the user.</summary>
    [Fact]
    public void NotificationCardWindow_BlockedApplyResult_SurfacesSafetyNoteAndDisablesAccept()
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            w.ApplyTemplate();
            var proposal = new ProfileOverrideProposal(
                Intent:        OverrideIntent.AdjustSensitivity,
                TargetField:   "sensitivity",
                ProposedValue: 0.8,
                Rationale:     "test",
                ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));
            w.ActiveProposal = proposal;

            w.ShowProposalApplyResult(ApplyResult.Blocked("vestibular advisory active"));

            Assert.Equal(Visibility.Visible, w.ProposalPanel.Visibility);
            Assert.Equal(Visibility.Visible, w.ProposalSafetyNoteText.Visibility);
            Assert.Contains("vestibular advisory active", w.ProposalSafetyNoteText.Text);
            Assert.False(w.ProposalAcceptBtn.IsEnabled);
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
