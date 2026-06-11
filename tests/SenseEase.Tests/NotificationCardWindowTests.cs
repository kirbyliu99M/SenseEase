using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using SenseEase.App.Localization;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Inference;
using SenseEase.Core.Safety;
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
[Collection("TestDataRoot")]
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
        RunOnStaCard(w =>
        {
            w.SetContent(TierColorPalette.TierBand.Medium, 3, 27.4);
            // The HUD title is the 5級 screen-motion status (the only user-facing status).
            // The card shows the BAND WORD only ("低") under its own 「目前螢幕狀態」 caption;
            // tier 3 is L3–4 → "低" (Calm). Polarity: low tier = calm screen.
            Assert.Equal("低", w.TitleText.Text);
            Assert.Equal("第 3 級 / 12　·　壓力 27", w.TelemetryText.Text);
        });
    }

    /// <summary>Test 2 (D3): IsPowerSaveActive overrides the numeric title
    /// with the bilingual "sensing paused" message; subsequent SetContent
    /// calls don't reveal the tier/pressure until the override clears.</summary>
    [Fact]
    public void NotificationCardWindow_SetContent_PowerSaveOverride()
    {
        RunOnStaCard(w =>
        {
            w.IsPowerSaveActive = true;
            w.SetContent(TierColorPalette.TierBand.VeryLow, 8, 89);
            // P2 (Pause/Resume mode-shift): band-only paused word under the 「目前螢幕狀態」 caption.
            Assert.Equal("已暫停", w.TitleText.Text);
            Assert.Equal("防護已暫停，點一下即可恢復。", w.BodyText.Text);
        });
    }

    /// <summary>Test 3 (D3): toggling the in-card CheckBox programmatically
    /// raises PowerSaveToggled with the new boolean value.</summary>
    [Fact]
    public void NotificationCardWindow_PowerSaveToggle_RaisesEvent()
    {
        RunOnStaCard(w =>
        {
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
        var original = LocalizationManager.Current.Language;
        try
        {
            // Pin zh so the assertion is deterministic regardless of a parallel class
            // toggling the process-wide language singleton.
            LocalizationManager.Current.Language = UILanguage.ChineseTraditional;
            RunOnStaCard(w =>
            {
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
                // UX-05 (decided 2026-06-11): the summary now uses plain consumer copy
                // (切換防護設定為…), NOT the raw "profile" engine token it carried before.
                Assert.Contains("切換防護設定", w.ProposalSummaryText.Text);
                Assert.DoesNotContain("profile", w.ProposalSummaryText.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("user reports easy nausea", w.ProposalRationaleText.Text);
                Assert.True(w.ProposalAcceptBtn.IsEnabled);

                w.ActiveProposal = null;
                Assert.Equal(Visibility.Collapsed, w.ProposalPanel.Visibility);
            });
        }
        finally
        {
            LocalizationManager.Current.Language = original;
        }
    }

    /// <summary>D3 Test B: clicking Accept raises <see cref="NotificationCardWindow.ProposalAccepted"/>
    /// with the active proposal payload. Mirrors how MainViewModel wires the
    /// event to <see cref="HabituationOverrideApplicator.ApplyAccepted"/>.</summary>
    [Fact]
    public void NotificationCardWindow_AcceptClick_RaisesProposalAcceptedWithPayload()
    {
        RunOnStaCard(w =>
        {
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
        RunOnStaCard(w =>
        {
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
        RunOnStaCard(w =>
        {
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

    /// <summary>EM5: the idle auto-fade must defer while a chatbot proposal is
    /// pending — otherwise the 60 s idle timer silently dismisses a card the user
    /// still has to accept / decline. Only the proposal's own expiry watchdog may
    /// retire it. Verified via the ShouldBeginIdleFade() decision seam (the live
    /// 60 s timer is impractical to drive in a unit test).</summary>
    [Fact]
    public void NotificationCardWindow_IdleFade_DeferredWhileProposalPending()
    {
        RunOnStaCard(w =>
        {
            // No proposal → idle fade is allowed.
            Assert.True(w.ShouldBeginIdleFade());

            w.ActiveProposal = new ProfileOverrideProposal(
                Intent:        OverrideIntent.SwitchProfile,
                TargetField:   "profile",
                ProposedValue: "SENSITIVE",
                Rationale:     "user reports easy nausea",
                ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));

            // Pending proposal → idle fade must defer (EM5).
            Assert.False(w.ShouldBeginIdleFade());

            // Once the proposal clears, the idle fade is allowed again.
            w.ActiveProposal = null;
            Assert.True(w.ShouldBeginIdleFade());
        });
    }

    // ── CW-5 (UX-02, locked invariant A1) — crisis-response legibility ───────

    /// <summary>CW-5 Test A: the crisis-detection seam exact-matches the fixed
    /// SafetyFloor crisis-line constants (either language) and nothing else, so a
    /// normal reply is never mis-routed to the non-dismissable crisis surface.</summary>
    [Fact]
    public void NotificationCardWindow_IsCrisisResponse_MatchesSafetyFloorConstantsOnly()
    {
        Assert.True(NotificationCardWindow.IsCrisisResponse(SafetyFloor.CrisisResponseZh));
        Assert.True(NotificationCardWindow.IsCrisisResponse(SafetyFloor.CrisisResponseEn));
        Assert.False(NotificationCardWindow.IsCrisisResponse("已套用。"));
        Assert.False(NotificationCardWindow.IsCrisisResponse(""));
        Assert.False(NotificationCardWindow.IsCrisisResponse(null));
        // A near-miss (trailing space) must NOT trip — the floor's wording is fixed.
        Assert.False(NotificationCardWindow.IsCrisisResponse(SafetyFloor.CrisisResponseEn + " "));
    }

    /// <summary>CW-5 Test B (locked invariant A1): showing the crisis response renders
    /// the FIXED SafetyFloor constant VERBATIM in a dedicated panel that is &gt;=12px,
    /// NOT clipped by a MaxHeight, NOT truncated, and NOT scrolled — every hotline
    /// character (988 / 1925 安心專線 / findahelpline.com) is always present. The card's
    /// auto-dismiss paths (idle fade + foreground change) are both suppressed while the
    /// panel shows; an explicit close clears the state.</summary>
    [Fact]
    public void NotificationCardWindow_CrisisResponse_FullyPresentedNoClipNoAutoDismiss()
    {
        RunOnStaCard(w =>
        {
            Assert.Equal(Visibility.Collapsed, w.CrisisPanel.Visibility);
            Assert.True(w.ShouldBeginIdleFade());   // no crisis → auto-dismiss allowed
            Assert.False(w.IsCrisisActive);

            w.ShowCrisisResponse(SafetyFloor.CrisisResponseEn);

            // Panel revealed, text set VERBATIM (never paraphrased / localized-away / trimmed).
            Assert.Equal(Visibility.Visible, w.CrisisPanel.Visibility);
            Assert.Equal(SafetyFloor.CrisisResponseEn, w.CrisisResponseText.Text);
            Assert.Contains("988", w.CrisisResponseText.Text);
            Assert.Contains("findahelpline.com", w.CrisisResponseText.Text);

            // A1 legibility: >=12px, no MaxHeight clip (default = PositiveInfinity), no
            // truncation. The general ChatStatusText carries the old 32px clip's
            // replacement (a scroll cap); the CRISIS text must stay fully uncapped.
            Assert.True(w.CrisisResponseText.FontSize >= 12);
            Assert.True(double.IsPositiveInfinity(w.CrisisResponseText.MaxHeight),
                "crisis text must not be height-clipped");
            Assert.Equal(TextTrimming.None, w.CrisisResponseText.TextTrimming);

            // A1 no-auto-dismiss: the idle-fade + foreground-change seam is suppressed.
            Assert.True(w.IsCrisisActive);
            Assert.False(w.ShouldBeginIdleFade());

            // Explicit close clears the crisis state and re-allows auto-dismiss.
            w.CrisisCloseBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Collapsed, w.CrisisPanel.Visibility);
            Assert.False(w.IsCrisisActive);
            Assert.True(w.ShouldBeginIdleFade());
        });
    }

    // ── UX-05 — proposal summary + expiry localize, no engine jargon ─────────

    /// <summary>UX-05 Test A: the proposal summary the user reads on the card LOCALIZES
    /// with the language toggle (it was zh-only before) AND uses plain consumer vocabulary
    /// instead of raw engine jargon (sensitivity / severe-strike-limit). The proposed
    /// value is the only verbatim token. Drives the private FormatProposalSummary via the
    /// public ActiveProposal setter and reads the rendered summary TextBlock.</summary>
    [Fact]
    public void NotificationCardWindow_ProposalSummary_LocalizesAndDropsEngineJargon()
    {
        var original = LocalizationManager.Current.Language;
        try
        {
            RunOnStaCard(w =>
            {
                var sensitivity = new ProfileOverrideProposal(
                    Intent:        OverrideIntent.AdjustSensitivity,
                    TargetField:   "personal_pressure_scale",
                    ProposedValue: 0.9,
                    Rationale:     "stable history",
                    ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));

                // zh: plain "防護敏感度" copy, NOT the raw "sensitivity" jargon.
                LocalizationManager.Current.Language = UILanguage.ChineseTraditional;
                w.ActiveProposal = null;
                w.ActiveProposal = sensitivity;
                string zh = w.ProposalSummaryText.Text;
                Assert.Contains("防護敏感度", zh);
                Assert.Contains("0.9", zh);
                Assert.DoesNotContain("sensitivity", zh, StringComparison.OrdinalIgnoreCase);

                // en: localizes to English plain copy (was stuck zh before the Loc() fix).
                LocalizationManager.Current.Language = UILanguage.English;
                w.ActiveProposal = null;
                w.ActiveProposal = sensitivity;
                string en = w.ProposalSummaryText.Text;
                Assert.Contains("Adjust protection sensitivity", en);
                Assert.Contains("0.9", en);
                Assert.DoesNotContain("把", en);          // no leftover zh
                Assert.DoesNotContain("severe-strike-limit", en, StringComparison.OrdinalIgnoreCase);

                // strike-limit intent uses the plain "strong-discomfort limit" term, not jargon.
                var strike = new ProfileOverrideProposal(
                    Intent:        OverrideIntent.AdjustStrikeLimit,
                    TargetField:   "severe_strike_limit",
                    ProposedValue: 3,
                    Rationale:     "too sensitive",
                    ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));
                w.ActiveProposal = null;
                w.ActiveProposal = strike;
                string strikeEn = w.ProposalSummaryText.Text;
                Assert.Contains("strong-discomfort limit", strikeEn);
                Assert.DoesNotContain("severe-strike-limit", strikeEn, StringComparison.OrdinalIgnoreCase);

                LocalizationManager.Current.Language = UILanguage.ChineseTraditional;
                w.ActiveProposal = null;
                w.ActiveProposal = strike;
                string strikeZh = w.ProposalSummaryText.Text;
                Assert.Contains("嚴重不適上限", strikeZh);
            });
        }
        finally
        {
            LocalizationManager.Current.Language = original;
        }
    }

    // ── UX-21 — blocked-apply reason renders FULLY localized (no half-translation) ──

    /// <summary>UX-21 Test A: a BlockedBySafety <see cref="ApplyResult"/> carrying a reason
    /// CODE renders calm, FULLY-localized copy on the safety note — never the English
    /// forensic Reason behind the localized "系統介入：" prefix. zh mode shows zh copy with
    /// no English words; en mode shows English copy with no leftover Chinese.</summary>
    [Fact]
    public void NotificationCardWindow_BlockedApplyResult_LocalizesReasonFromCode()
    {
        var original = LocalizationManager.Current.Language;
        try
        {
            RunOnStaCard(w =>
            {
                var proposal = new ProfileOverrideProposal(
                    Intent:        OverrideIntent.AdjustSensitivity,
                    TargetField:   "sensitivity",
                    ProposedValue: 0.8,
                    Rationale:     "test",
                    ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));

                // A coded block result — the same shape the applicator returns. The English
                // Reason is the forensic string the event log keeps; the Card must NOT show it.
                var blocked = ApplyResult.Blocked(
                    "vestibular advisory active; relaxation proposals require manual Settings change",
                    HabituationInterceptCode.VestibularRelaxation);

                // zh: fully Chinese, no English forensic words leak through.
                LocalizationManager.Current.Language = UILanguage.ChineseTraditional;
                w.ActiveProposal = proposal;
                w.ShowProposalApplyResult(blocked);
                string zh = w.ProposalSafetyNoteText.Text;
                Assert.Equal(Visibility.Visible, w.ProposalSafetyNoteText.Visibility);
                Assert.Contains("前庭", zh);
                Assert.DoesNotContain("vestibular", zh, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("manual Settings change", zh);
                Assert.False(w.ProposalAcceptBtn.IsEnabled);

                // en: localizes to English plain copy (was stuck on the raw Reason before).
                LocalizationManager.Current.Language = UILanguage.English;
                w.ActiveProposal = proposal;
                w.ShowProposalApplyResult(blocked);
                string en = w.ProposalSafetyNoteText.Text;
                Assert.Contains("Vestibular protection is on", en);
                Assert.DoesNotContain("前庭", en); // no leftover Chinese
            });
        }
        finally
        {
            LocalizationManager.Current.Language = original;
        }
    }

    /// <summary>UX-21 Test B: a BlockedBySafety result with NO recognized code
    /// (<see cref="HabituationInterceptCode.None"/>) falls back to the literal Reason so
    /// nothing is ever dropped. Pins the back-compat path the older Card test relied on.</summary>
    [Fact]
    public void NotificationCardWindow_BlockedApplyResult_NoCode_FallsBackToLiteralReason()
    {
        RunOnStaCard(w =>
        {
            var proposal = new ProfileOverrideProposal(
                Intent:        OverrideIntent.AdjustSensitivity,
                TargetField:   "sensitivity",
                ProposedValue: 0.8,
                Rationale:     "test",
                ExpiresAtUtc:  DateTime.UtcNow.AddHours(2));
            w.ActiveProposal = proposal;

            // Reason-only overload → Code is None → the literal Reason is surfaced verbatim.
            w.ShowProposalApplyResult(ApplyResult.Blocked("custom non-coded reason"));

            Assert.Equal(Visibility.Visible, w.ProposalSafetyNoteText.Visibility);
            Assert.Contains("custom non-coded reason", w.ProposalSafetyNoteText.Text);
            Assert.False(w.ProposalAcceptBtn.IsEnabled);
        });
    }

    // ── UX-10 — typography floor: no 10/11px text in the card XAML ───────────

    /// <summary>UX-10 Test: the card XAML carries NO FontSize="10|11" — the locked
    /// typography floor (body 14, caption 12; no 10/11px text on a surface a nauseated
    /// user reads). String-scan over the source XAML, mirroring the DesignLintTests
    /// file-walk pattern (objective, build-independent).</summary>
    [Fact]
    public void NotificationCardWindow_Xaml_HasNoSub12FontSizes()
    {
        string xaml = ReadCardXamlSource();
        // Match FontSize="10" / "11" (and a "10.5"/"11.x" just in case) but NOT "110"/"12".
        var bad = new Regex(@"FontSize\s*=\s*""1[01](\.\d+)?""", RegexOptions.Compiled);
        var matches = bad.Matches(xaml);
        Assert.True(matches.Count == 0,
            "NotificationCardWindow.xaml still has sub-12px FontSize occurrences (UX-10 floor): "
            + string.Join(", ", System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Cast<Match>(matches), m => m.Value)));
    }

    /// <summary>Locate the card XAML source by walking up from the test bin dir to the
    /// repo's src/SenseEase.App, mirroring DesignLintTests.FindAppDir.</summary>
    private static string ReadCardXamlSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(
                dir.FullName, "src", "SenseEase.App", "Views", "NotificationCardWindow.xaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate src/SenseEase.App/Views/NotificationCardWindow.xaml from "
            + AppContext.BaseDirectory);
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

    /// <summary>Card-scoped STA helper: constructs a <see cref="NotificationCardWindow"/>,
    /// force-applies its template (so the named parts are non-null), runs <paramref name="body"/>
    /// with it, and ALWAYS closes the window before the STA thread exits.
    ///
    /// <para>Why the close matters: the card subscribes to the process-wide
    /// <see cref="LocalizationManager.Current"/> in its constructor and only unsubscribes on
    /// <c>Closed</c>. The STA thread runs the body then exits WITHOUT pumping a dispatcher, so a
    /// window left open would outlive its (now-dead) dispatcher thread while STILL subscribed to
    /// the singleton. A LATER test on another thread that flips
    /// <c>LocalizationManager.Current.Language</c> (e.g.
    /// <c>ChatbotViewModelStatusCopyTests.WithLanguage</c>) would then synchronously fire that
    /// leaked handler, which touches this window's WPF DependencyObjects from the wrong thread and
    /// throws a cross-thread <see cref="InvalidOperationException"/>. Closing the card here runs
    /// its <c>Closed</c> handler, dropping the subscription so the singleton list stays clean
    /// across the whole run.</para></summary>
    private static void RunOnStaCard(Action<NotificationCardWindow> body)
    {
        RunOnSta(() =>
        {
            var w = new NotificationCardWindow();
            // Force template instantiation so the named parts (TitleText, ProposalPanel, …)
            // are non-null before the body touches them.
            w.ApplyTemplate();
            try { body(w); }
            finally
            {
                // Closed → unsubscribe from LocalizationManager (see summary). Best-effort:
                // never let cleanup mask the body's real assertion failure.
                try { w.Close(); } catch { /* best-effort cleanup */ }
            }
        });
    }
}
