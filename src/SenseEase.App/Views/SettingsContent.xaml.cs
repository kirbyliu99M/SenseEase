using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
using SenseEase.App.Theme;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.Views;

/// <summary>
/// UI Consolidation Sprint M-Phase-1 (2026-05-30): shared settings UserControl.
/// Hosted by both <see cref="SettingsWindow"/> (tray menu / Pop-up Card gear
/// path) AND the main console's "設定" TabItem. State sharing is automatic
/// via the static <see cref="UserPreferences.Current"/> + <c>AppConfig.*</c>
/// singletons — no DI plumbing required.
///
/// <para>Cross-instance UI sync: <see cref="UserPreferences.Changed"/> fires
/// after every successful Save; each loaded instance subscribes and
/// re-hydrates its checkbox/radio state from <see cref="UserPreferences.Current"/>
/// so a toggle flipped on Tab A propagates visually to Tab B (and vice
/// versa) without explicit cross-wiring.</para>
///
/// <para>Vestibular guard: <see cref="HasVestibularDiagnosis"/> is a
/// host-provided Func read at <see cref="OnLoaded"/> and again whenever
/// <see cref="RefreshFromPreferences"/> is invoked. Matches the M8 pattern
/// (M8 added the lookup as a Func because Habituation engine state must stay
/// read-only-consumed from this surface, per the locked decision in the
/// Habituation sprint).</para>
///
/// <para>All toggle logic was lifted as-is from the pre-Phase-1
/// SettingsWindow.xaml.cs — the Window now hosts THIS UserControl and
/// forwards <see cref="PreferencesChanged"/>.</para>
/// </summary>
public partial class SettingsContent : UserControl
{
    private bool _wiring; // suppress save while we initialise the checkboxes

    /// <summary>TASK #7: true while the 唤出热键 / Reveal hotkey row is armed to capture the next
    /// modifier+key chord (the user clicked 录制 / Record and the capture box has focus). The
    /// PreviewKeyDown handler only consumes keys while this is set, so ordinary typing elsewhere
    /// is never swallowed.</summary>
    private bool _capturingHotkey;

    /// <summary>Host-provided lookup for the vestibular-diagnosis flag (M8
    /// pattern — read-only consumption of Habituation engine state). Defaults
    /// to "no diagnosis" when the host hasn't wired it. Set this BEFORE the
    /// UserControl loads; OnLoaded reads it once during hydration.</summary>
    public Func<bool>? HasVestibularDiagnosis { get; set; }

    /// <summary>Alpha-2 addendum (auto-profiling world): host-provided read of the
    /// PERSISTENT vestibular protection state for the Settings "regret" toggle's checked
    /// state. Wired by MainWindow to MainViewModel.VestibularProtectionEnabled.</summary>
    public Func<bool>? GetVestibularProtection { get; set; }

    /// <summary>Alpha-2 addendum: host-provided write of the vestibular protection toggle.
    /// Wired by MainWindow to MainViewModel.VestibularProtectionEnabled (which drives the
    /// engine + persists). Read-only-consumed elsewhere; this is the ONE deliberate write
    /// (the regret mechanism), gated to the auto-profiling world.</summary>
    public Action<bool>? SetVestibularProtection { get; set; }

    /// <summary>Alpha-2 addendum: host-provided "reset personalisation state" action for the
    /// Reset that moved from the console into Settings. Wired by MainWindow to
    /// MainViewModel.ResetStateCommand (Q4.5-safe — preserves the habituation offset).</summary>
    public Action? ResetRequested { get; set; }

    /// <summary>D-1 (CW-1 step 3, 2026-06-11): host-provided read of the graded-exposure
    /// (habituation) opt-in for the 漸進適應 / Graded exposure toggle's checked state.
    /// Wired by the host (MainWindow) to MainViewModel.GradedExposureEnabled (true =
    /// program running, false = paused). When null (e.g. the notification-card quick
    /// Settings popup, which has no MainViewModel) the toggle hydrates from the persisted
    /// pref instead. Read-only consumption — the engine flip is the host's job via the
    /// matching <see cref="SetGradedExposure"/> action.</summary>
    public Func<bool>? GetGradedExposure { get; set; }

    /// <summary>D-1: host-provided write of the graded-exposure toggle. Wired by the host
    /// (MainWindow) to MainViewModel.GradedExposureEnabled, which flips the engine pause
    /// AND persists the pref. This is the ONE legitimate user-intent flip of the habituation
    /// pause (the old dashboard toggle was removed 2026-06-09). When null the handler still
    /// persists <see cref="UserPreferences.HabituationEnabled"/> directly so the choice
    /// survives restart; the engine then picks it up at next launch (construction-time
    /// hydration). Q4.5-safe — never clears the earned habituation offset.</summary>
    public Action<bool>? SetGradedExposure { get; set; }

    /// <summary>TASK #7 (UI-RESHAPE): host-provided read of the current reveal-popup global
    /// hotkey gesture (e.g. "Win+Alt+S"), for the 唤出热键 / Reveal hotkey row's displayed value.
    /// When null (e.g. the notification-card quick-Settings popup, which has no App-level shell)
    /// the row hydrates from the persisted <see cref="UserPreferences.RevealHotkey"/> pref instead.
    /// Read-only consumption — the live re-register is the host's job via the matching
    /// <see cref="SetRevealHotkey"/> action.</summary>
    public Func<string>? GetRevealHotkey { get; set; }

    /// <summary>TASK #7: host-provided write of a newly-recorded reveal-popup hotkey gesture.
    /// Wired by the host (App via MainWindow) to <c>App.ReRegisterRevealHotkey</c>, which
    /// re-registers the live Win32 hotkey from the just-saved pref. This handler ALWAYS persists
    /// <see cref="UserPreferences.RevealHotkey"/> + Saves first, then invokes this so the live
    /// binding matches; when null (no-shell popup path) the choice still survives restart (the
    /// App registers it from the pref at next launch). Mirrors the GradedExposure host-delegate
    /// + null-fallback pattern.</summary>
    public Action<string>? SetRevealHotkey { get; set; }

    /// <summary>Host-provided action that opens the dedicated Event Log window
    /// (2026-06-07: the Event Log moved out of a top-level tab into Settings).
    /// Wired by the host that owns the MainViewModel; when null (e.g. the quick
    /// Settings popup from the notification card) the Event Log card hides itself
    /// in <see cref="OnLoaded"/>.</summary>
    public Action? OpenEventLog { get; set; }

    /// <summary>FEAT-08 (CW-10, B1b UI half): host-provided read of the live
    /// head-tracking health (<see cref="HeadTrackingHealth"/>) for the one-line
    /// status caption in the 頭部追蹤與隱私 group. Wired by BOTH hosts (MainWindow's
    /// 設定 tab + the tray/gear <see cref="SettingsWindow"/>) to the
    /// <see cref="SidecarHeadPoseReader"/> instance that lives on MainViewModel —
    /// the same host-delegate pattern the GradedExposure / Vestibular toggles use.
    /// When null (e.g. the notification-card quick-Settings popup, which has no
    /// MainViewModel and therefore no reader) the status row stays hidden — there is
    /// no honest health to show there. Read-only consumption; surfacing-only.</summary>
    public Func<HeadTrackingHealth>? GetHeadTrackingHealth { get; set; }

    /// <summary>UX-08 (B1b): the value of <see cref="UserPreferences.HeadTrackingEnabled"/>
    /// the APP started with, captured ONCE the first time any
    /// <see cref="SettingsContent"/> is constructed (process start, before the user can
    /// have toggled anything). The Restart-needed chip shows ONLY when the live toggle
    /// differs from this — i.e. a real pending restart — instead of on every Settings
    /// open. Static + lazily-captured so the tab instance and the tray/gear-window
    /// instance share ONE app-start baseline (re-opening Settings must not "re-baseline"
    /// the comparison and hide a genuine pending change).</summary>
    private static bool? _appStartHeadTracking;

    /// <summary>Raised after any persisted toggle change so the host can
    /// repaint dependent surfaces (bar colour, tray icon) immediately
    /// instead of waiting for the next StateChanged push. Mirrors the
    /// pre-Phase-1 SettingsWindow event so existing host plumbing
    /// (MainWindow.ShowSettingsWindow + NotificationCardWindow.OnSettingsClicked)
    /// continues to work after the refactor.</summary>
    public event EventHandler? PreferencesChanged;

    public SettingsContent()
    {
        InitializeComponent();
        // UX-08: snapshot the app-start head-tracking value ONCE, the first time
        // any SettingsContent is constructed — this is process start, before the
        // user can have flipped the toggle. Subsequent instances reuse it so the
        // Restart-needed chip compares against the value the app actually launched
        // with, not against whatever the toggle is when Settings is re-opened.
        _appStartHeadTracking ??= UserPreferences.Current.HeadTrackingEnabled;
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>UX-08 test seam: the app-start head-tracking baseline the Restart
    /// chip compares against. Exposed internal so the B1b chip tests can pin both
    /// the pending (toggle ≠ baseline ⇒ chip shown) and not-pending (toggle ==
    /// baseline ⇒ chip hidden) branches without spinning a whole app lifecycle.
    /// Falls back to the persisted value if no instance has been constructed yet
    /// (e.g. a test that calls the static decision directly).</summary>
    internal static bool AppStartHeadTracking =>
        _appStartHeadTracking ?? UserPreferences.Current.HeadTrackingEnabled;

    /// <summary>UX-08 test seam: force the app-start baseline (and the
    /// once-captured guard) so the chip-logic tests are deterministic regardless of
    /// whether an earlier test already constructed a <see cref="SettingsContent"/>
    /// and fixed the process-wide baseline. PRODUCTION never calls this — the
    /// constructor's null-coalescing capture is the only writer at runtime.</summary>
    internal static void SetAppStartHeadTrackingForTesting(bool value) =>
        _appStartHeadTracking = value;

    /// <summary>UX-08 test seam (and the single source of truth the code-behind
    /// uses): the Restart-needed chip should be VISIBLE iff the live toggle value
    /// differs from the app-start baseline — i.e. a real pending restart. Pure +
    /// static so the chip tests pin the logic directly.</summary>
    internal static bool ShouldShowRestartChip(bool currentHeadTracking) =>
        currentHeadTracking != AppStartHeadTracking;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // GLASS BRIDGE (2026-06-18): the Settings now wears the popup's glass palette
        // (SettingsGlassPalette.xaml is merged in our XAML; this tracks the Game-Bar dark variant +
        // re-applies on theme flips). Replaces ThemeManager.TrackElement here — that merged the GREY
        // Palette.Dark and would fight the glass keys. The glass is ALWAYS on (slate by default),
        // matching the popup, regardless of the OS light/dark. Unregistered in OnUnloaded.
        SettingsGlassTheme.Track(this);

        // Event Log card (2026-06-07): only shown where the host wired OpenEventLog
        // (it needs the MainViewModel-backed window). Hidden in the notification
        // card's quick-Settings popup, which has no MainViewModel.
        EventLogCard.Visibility = OpenEventLog is null ? Visibility.Collapsed : Visibility.Visible;

        RefreshFromPreferences();
        UserPreferences.Changed += OnPreferencesChangedExternally;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SettingsGlassTheme.Untrack(this);
        UserPreferences.Changed -= OnPreferencesChangedExternally;
    }

    /// <summary>Re-hydrate every toggle/radio from the current persisted
    /// state. Called once on Loaded and again whenever a peer
    /// <see cref="SettingsContent"/> instance (or chatbot intent) writes to
    /// preferences. The <see cref="_wiring"/> guard prevents the
    /// Checked/Unchecked handlers from re-saving during the hydration pass
    /// (they'd see _wiring=true and bail out).</summary>
    public void RefreshFromPreferences()
    {
        _wiring = true;
        try
        {
            // Appearance setting (2026-06-07): hydrate the 自動/淺色/深色 segmented
            // control from the persisted ThemeMode. Guarded by _wiring so the
            // Checked handler (OnThemeModeChanged) does NOT re-enter SetMode
            // during hydration.
            switch (UserPreferences.Current.ThemeMode)
            {
                case AppThemeMode.Light: ThemeLight.IsChecked = true; break;
                case AppThemeMode.Dark:  ThemeDark.IsChecked  = true; break;
                default:                 ThemeAuto.IsChecked  = true; break; // Auto
            }

            // Language setting (2026-06-07): hydrate the 繁體中文 / English segmented
            // control from the persisted language_mode. Guarded by _wiring so the
            // Checked handler (OnLanguageModeChanged) does NOT re-enter the
            // LocalizationManager / Save path during hydration. Tolerant parse
            // mirrors the ThemeMode posture — unknown values default to Chinese.
            if (LocalizationManager.Parse(UserPreferences.Current.LanguageModeRaw)
                == UILanguage.English)
                LanguageEnglish.IsChecked = true;
            else
                LanguageChinese.IsChecked = true;

            // TASK #7: hydrate the 唤出热键 / Reveal hotkey display from the host's live value
            // (or the persisted pref on the no-shell popup path). Guarded by _wiring — this is a
            // read-only TextBlock update so it never re-enters a save, but kept consistent.
            RevealHotkeyDisplay.Text =
                GetRevealHotkey?.Invoke() ?? UserPreferences.Current.RevealHotkey;

            ToggleMinimizeToTray.IsChecked = UserPreferences.Current.MinimizeToTrayOnClose;
            // Prefer the registry's actual state for run-at-startup so an
            // externally-edited registry doesn't desync from what we display.
            ToggleRunAtStartup.IsChecked   = AutoStartRegistry.IsEnabled();

            ToggleHeadTrackingEnabled.IsChecked =
                UserPreferences.Current.HeadTrackingEnabled;

            // Beta-1 D-16: hydrate the per-app attribution consent toggle. Guarded
            // by _wiring so this assignment does not re-enter the Save path.
            TogglePerAppAttribution.IsChecked =
                UserPreferences.Current.PerAppAttributionEnabled;

            // Phase-1: hydrate the proactive contextual check-in opt-in. Checked == Enabled; both
            // Unset (not yet asked) and Declined render unchecked. Guarded by _wiring.
            ToggleProactiveCheckIns.IsChecked =
                UserPreferences.Current.ProactiveCheckInsConsent == ProactiveCheckInConsent.Enabled;

            // B5: hydrate the auto-adaptation opt-in. Checked == Enabled; Unset/Declined render unchecked.
            ToggleAdaptiveAutoTune.IsChecked =
                UserPreferences.Current.AdaptiveAutoTuneConsent == ProactiveCheckInConsent.Enabled;

            // UX-08: show the Restart-needed chip only on a genuine pending change
            // (toggle ≠ the value the app started with), not on every Settings open.
            UpdateRestartChip();

            // FEAT-08 (B1b UI half): surface the live sidecar health (or hide the
            // row where no reader exists). Read-only consumption of the host reader.
            RefreshHeadTrackingHealth();

            // (Head-tracking follow mode is fixed to FullFollow now — no radios to hydrate.)

            // D-1 (CW-1 step 3, 2026-06-11): hydrate the 漸進適應 / Graded exposure toggle.
            // Prefer the host's live engine state (true = running, false = paused) when
            // wired; fall back to the persisted opt-in on the no-MainViewModel popup path.
            // Guarded by _wiring so the Checked/Unchecked handler does not re-enter the
            // engine/Save path during hydration.
            ToggleGradedExposure.IsChecked =
                GetGradedExposure?.Invoke() ?? UserPreferences.Current.HabituationEnabled;

            // Alpha-2 addendum: the vestibular "regret" toggle + the moved Reset are shown
            // ONLY in the auto-profiling world AND only when the host wired the callbacks
            // (the NotificationCard→Settings path doesn't, so it won't show an inert group).
            // Hydrate the toggle from the host's current engine state (read-only consumption).
            AutoProfilingDataGroup.Visibility =
                (AppConfig.AutoProfilingEnabled && SetVestibularProtection is not null)
                    ? Visibility.Visible : Visibility.Collapsed;
            VestibularProtectionToggle.IsChecked = GetVestibularProtection?.Invoke() ?? false;
        }
        finally { _wiring = false; }
    }

    private void OnPreferencesChangedExternally(object? sender, EventArgs e)
    {
        // Fired by UserPreferences.Save() from ANY surface. If WE were the
        // sender (i.e. our own handler just saved), our checkbox state is
        // already in sync — but re-hydrating is idempotent under _wiring,
        // so we accept the small redundant work in favour of simpler
        // subscriber logic. A peer instance saving a change reaches this
        // handler with a different effective source; the re-hydrate
        // propagates the new state to our visible UI.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshFromPreferences));
            return;
        }
        RefreshFromPreferences();
    }

    // #5 (2026-06-21): OnToggleAutoChanged removed with the 通知與顯示 / 主動提醒 toggle. The
    // AutoExpandNotifications pref is retained (defaults off → auto-show inert) until the
    // NotificationCard retirement removes it; see docs/NOTIFICATIONCARD_RETIREMENT_PLAN.md.

    private void OnToggleProactiveCheckInsChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        // Settings is the manual control: checked == Enabled, unchecked == Declined (an explicit
        // "no", which also stops the first-run consent toast from reappearing).
        UserPreferences.Current.ProactiveCheckInsConsent = ToggleProactiveCheckIns.IsChecked == true
            ? ProactiveCheckInConsent.Enabled
            : ProactiveCheckInConsent.Declined;
        UserPreferences.Current.Save();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleAdaptiveAutoTuneChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        // B5: Settings is the manual control — checked == Enabled (turn the auto-adaptation loop on),
        // unchecked == Declined (an explicit off). Default stays Unset until the user chooses here.
        UserPreferences.Current.AdaptiveAutoTuneConsent = ToggleAdaptiveAutoTune.IsChecked == true
            ? ProactiveCheckInConsent.Enabled
            : ProactiveCheckInConsent.Declined;
        UserPreferences.Current.Save();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleMinimizeToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        UserPreferences.Current.MinimizeToTrayOnClose = ToggleMinimizeToTray.IsChecked == true;
        UserPreferences.Current.Save();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleRunAtStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = ToggleRunAtStartup.IsChecked == true;
        // Write the registry FIRST. Surface failure by reverting visually.
        bool ok = AutoStartRegistry.SetEnabled(desired);
        if (!ok)
        {
            _wiring = true;
            ToggleRunAtStartup.IsChecked = !desired;
            _wiring = false;
            ThemedDialog.Notify(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "無法寫入登入啟動設定。可能的原因：\n" +
                    "- 系統管理員或安全性軟體限制了登入啟動項目。\n" +
                    "- 註冊表權限不足。\n\n" +
                    "你可以手動把 SenseEase 加到 Windows「啟動」資料夾作為替代方案。",
                    "Couldn't save the run-at-startup setting. Possible causes:\n" +
                    "- An administrator or security software is blocking startup entries.\n" +
                    "- Insufficient registry permissions.\n\n" +
                    "As a workaround, add SenseEase to the Windows Startup folder manually."),
                DialogKind.Warning);
            return;
        }
        UserPreferences.Current.RunAtWindowsStartup = desired;
        UserPreferences.Current.Save();
    }

    private void OnToggleHeadTrackingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = ToggleHeadTrackingEnabled.IsChecked == true;
        UserPreferences.Current.HeadTrackingEnabled = desired;
        UserPreferences.Current.Save();
        AppConfig.HeadTrackingEnabled = desired;
        // UX-08: a flip toward (or back to) the app-start value updates the pending
        // chip — flipping back to the launched value hides it again (no fake restart).
        UpdateRestartChip();
    }

    /// <summary>Beta-1 D-16 (2026-06-14): per-app attribution consent toggle.
    /// Unlike head-tracking (startup-bound), this is runtime-live: it writes the
    /// volatile <see cref="WindowsActivityFilter.PerAppAttributionEnabled"/> flag
    /// the hook thread reads on the next foreground change, so consent takes (or
    /// drops) effect immediately — no restart chip. STRICTLY on-device (D-16): the
    /// flag only governs whether the foreground app name is stamped locally.</summary>
    private void OnTogglePerAppAttributionChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = TogglePerAppAttribution.IsChecked == true;
        UserPreferences.Current.PerAppAttributionEnabled = desired;
        UserPreferences.Current.Save();
        AppConfig.PerAppAttributionEnabled = desired;
        // Apply live so the next foreground change is (or stops being) attributed
        // without waiting for a relaunch.
        SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.PerAppAttributionEnabled
            = desired;
    }

    /// <summary>Beta-1 D-17 (2026-06-14): export a de-identified clinical summary the user can
    /// hand to a clinician. The numeric aggregation (measured vs estimated kept separate, raw app
    /// names stripped to category) lives in <see cref="ClinicalExportService"/>; this handler just
    /// runs the Save dialog and surfaces success/failure. The export is user-initiated, so unlike
    /// the best-effort telemetry writers a failure is reported, not swallowed.</summary>
    private void OnExportClinicalClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title        = LocalizationManager.Current.Pick("匯出健康報告", "Export health report"),
            Filter       = "JSON (*.json)|*.json",
            FileName     = $"senseease-health-export-{DateTime.UtcNow:yyyyMMdd}.json",
            AddExtension = true,
            DefaultExt   = ".json",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var svc = new ClinicalExportService(
                new FileSymptomEventStore(AppConfig.SymptomLogPath));
            svc.WriteJson(dlg.FileName, DateTime.UtcNow);
            ThemedDialog.Notify(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "已匯出去識別化的健康報告（僅數字與類別）。",
                    "Exported a de-identified health report (numbers and categories only)."),
                DialogKind.Info);
        }
        catch
        {
            ThemedDialog.Notify(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "匯出失敗。請再試一次，或改存到其他位置（例如桌面）。",
                    "Export failed. Please try again, or save to another location (such as your Desktop)."),
                DialogKind.Error);
        }
    }

    /// <summary>UX-08 (B1b): show the Restart-needed chip ONLY when the live
    /// head-tracking toggle differs from the value the app launched with (a real
    /// pending restart). Flipping the toggle back to the app-start value hides the
    /// chip again. Replaces the old always-visible chip that lied on every open.</summary>
    private void UpdateRestartChip()
    {
        bool current = ToggleHeadTrackingEnabled.IsChecked == true;
        RestartButton.Visibility = ShouldShowRestartChip(current)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>FEAT-08 (CW-10, B1b UI half): paint the one-line head-tracking
    /// health caption from the host-wired reader's live state. The row is hidden
    /// where no reader exists (the no-MainViewModel popup) — there is no honest
    /// health to show. Status is conveyed by a leading glyph + a plain word + a
    /// theme token, never colour alone. Tier→token mapping reuses the existing
    /// Comfort tokens (calm/rising/high) the rest of the app already uses.</summary>
    private void RefreshHeadTrackingHealth()
    {
        if (GetHeadTrackingHealth is null)
        {
            HeadTrackingHealthRow.Visibility = Visibility.Collapsed;
            return;
        }

        HeadTrackingHealth health;
        try { health = GetHeadTrackingHealth(); }
        catch { HeadTrackingHealthRow.Visibility = Visibility.Collapsed; return; }

        // (glyph, localized label, foreground token). The glyph is what carries the
        // state for users who can't distinguish the hue — the "never colour alone"
        // invariant. Glyphs are plain Unicode marks so any UI font renders them.
        string glyph;
        string label;
        string tokenKey;
        switch (health)
        {
            case HeadTrackingHealth.Healthy:
                glyph    = "●"; // ● filled dot
                label    = LocalizationManager.Current.Pick("運作中", "Working");
                tokenKey = "ComfortSteadyText";
                break;
            case HeadTrackingHealth.NoFrames:
                glyph    = "○"; // ○ hollow dot
                label    = LocalizationManager.Current.Pick("攝影機無畫面", "No camera image");
                tokenKey = "ComfortRisingText";
                break;
            default: // Dead
                glyph    = "✕"; // ✕ multiplication x
                label    = LocalizationManager.Current.Pick("已停止", "Stopped");
                tokenKey = "ComfortHighText";
                break;
        }

        HeadTrackingHealthGlyph.Text = glyph;
        HeadTrackingHealthText.Text  = label;
        // Resolve the token through the control's live (theme-tracked) resources so
        // it follows light/dark like the rest of this surface. Fall back to
        // TextSecondary if a token is ever missing (never crash a status caption).
        var brush = TryFindResource(tokenKey) ?? TryFindResource("TextSecondary");
        if (brush is System.Windows.Media.Brush b)
        {
            HeadTrackingHealthGlyph.Foreground = b;
            HeadTrackingHealthText.Foreground  = b;
        }
        HeadTrackingHealthRow.Visibility = Visibility.Visible;
    }

    /// <summary>D-1 (CW-1 step 3, 2026-06-11): the 漸進適應 / Graded exposure toggle changed.
    /// This is the SOLE user-intent path that resumes/pauses the habituation program (the
    /// old dashboard toggle was removed 2026-06-09). Routes the new value to the host
    /// (MainViewModel.GradedExposureEnabled), which flips the engine pause immediately AND
    /// persists the pref. On the no-MainViewModel popup path (SetGradedExposure unwired) we
    /// still persist the pref directly so the choice survives restart; the engine picks it up
    /// at next launch via construction-time hydration. Suppressed during hydration
    /// (<see cref="_wiring"/>). Q4.5-safe — never clears the earned habituation offset.</summary>
    private void OnToggleGradedExposureChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = ToggleGradedExposure.IsChecked == true;
        if (SetGradedExposure is not null)
        {
            SetGradedExposure.Invoke(desired); // host flips the engine pause + persists
        }
        else
        {
            // No host engine on this surface — persist so a later launch hydrates the pause.
            UserPreferences.Current.HabituationEnabled = desired;
            UserPreferences.Current.Save();
        }
    }

    /// <summary>TASK #7: arm the reveal-hotkey capture. Focuses the capture box and flips
    /// <see cref="_capturingHotkey"/> on so the next modifier+key chord is recorded by
    /// <see cref="OnHotkeyCaptureKeyDown"/>. The button label/box hint flip to a "press a
    /// combination" prompt; Escape (handled in the capture box) cancels without changing
    /// anything.</summary>
    private void OnRecordHotkeyClicked(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        RevealHotkeyDisplay.Text = LocalizationManager.Current.Pick("按下組合鍵…", "Press a combination…");
        HotkeyCaptureBox.Focus();
    }

    /// <summary>TASK #7: capture the next modifier+key chord while armed. Ignores modifier-only
    /// presses (waits for a real key), cancels on Escape, and on a valid chord builds the gesture
    /// via <see cref="Services.GlobalHotkeyService.Format"/>, persists
    /// <see cref="UserPreferences.RevealHotkey"/> + Save, then asks the host to re-register the
    /// live hotkey via <see cref="SetRevealHotkey"/> (with a null-fallback that still persists so
    /// the choice survives restart). Marked Handled so the chord never leaks to other handlers.</summary>
    private void OnHotkeyCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;

        // The real key may arrive as a system key (e.g. while Alt is held); unwrap it.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Escape cancels the capture without changing the saved gesture.
        if (key == Key.Escape)
        {
            e.Handled = true;
            EndHotkeyCapture();
            return;
        }

        // Ignore modifier-only presses — keep waiting for the actual key that completes the chord.
        if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin
                or Key.None)
            return;

        // Build the Win32 modifier mask from the live keyboard modifiers.
        uint mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= Services.GlobalHotkeyService.MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0)     mods |= Services.GlobalHotkeyService.MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0)   mods |= Services.GlobalHotkeyService.MOD_SHIFT;
        if ((m & ModifierKeys.Windows) != 0) mods |= Services.GlobalHotkeyService.MOD_WIN;

        // Require at least one modifier — a bare key is not a valid global hotkey.
        if (mods == 0)
        {
            e.Handled = true; // consume but keep capturing — prompt the user for a modifier
            RevealHotkeyDisplay.Text =
                LocalizationManager.Current.Pick("請加上 Ctrl / Alt / Shift / Win…", "Add Ctrl / Alt / Shift / Win…");
            return;
        }

        e.Handled = true;
        string gesture = Services.GlobalHotkeyService.Format(mods, key);

        // Persist first so the host re-registers from a saved value (and so the no-shell popup
        // path still records the choice for next launch).
        UserPreferences.Current.RevealHotkey = gesture;
        UserPreferences.Current.Save();

        // Ask the host to re-register the live Win32 hotkey from the just-saved pref. Null on the
        // no-shell popup path — the App picks the new value up at next launch.
        SetRevealHotkey?.Invoke(gesture);

        EndHotkeyCapture();
        // Reflect the final (possibly host-confirmed) value.
        RevealHotkeyDisplay.Text = GetRevealHotkey?.Invoke() ?? gesture;
    }

    /// <summary>TASK #7: disarm capture and restore the displayed gesture. Lost focus also ends a
    /// capture (LostKeyboardFocus → this) so an abandoned capture never stays armed.</summary>
    private void OnHotkeyCaptureLostFocus(object sender, RoutedEventArgs e) => EndHotkeyCapture();

    private void EndHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        RevealHotkeyDisplay.Text = GetRevealHotkey?.Invoke() ?? UserPreferences.Current.RevealHotkey;
    }

    /// <summary>Appearance setting (2026-06-07): the 自動 / 淺色 / 深色 segmented
    /// control changed. Maps the checked segment to a
    /// <see cref="ThemeManager.ThemeMode"/> and hands it to
    /// <see cref="ThemeManager.SetMode"/>, which persists the choice (via
    /// <see cref="UserPreferences.ThemeMode"/>) and re-applies the theme to
    /// every open window immediately. The <see cref="_wiring"/> guard skips this
    /// during hydration so re-checking a segment in
    /// <see cref="RefreshFromPreferences"/> doesn't loop back into SetMode.
    /// <para>RadioButton.Checked fires only for the newly-selected segment, so a
    /// single user pick raises this exactly once.</para></summary>
    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;

        ThemeManager.ThemeMode mode;
        if (ThemeLight.IsChecked == true)     mode = ThemeManager.ThemeMode.Light;
        else if (ThemeDark.IsChecked == true) mode = ThemeManager.ThemeMode.Dark;
        else                                  mode = ThemeManager.ThemeMode.Auto;

        ThemeManager.SetMode(mode);
    }

    /// <summary>Language setting (2026-06-07): the 繁體中文 / English segmented
    /// control changed. Maps the checked segment to a
    /// <see cref="UILanguage"/>, sets
    /// <see cref="LocalizationManager.Current"/>.<see cref="LocalizationManager.Language"/>
    /// (so every <c>{loc:T}</c> binding re-picks its half live, no restart) AND
    /// persists the choice via <see cref="UserPreferences.LanguageModeRaw"/> +
    /// <see cref="UserPreferences.Save"/>. The <see cref="_wiring"/> guard skips
    /// this during hydration so re-checking a segment in
    /// <see cref="RefreshFromPreferences"/> doesn't loop back into Save.
    /// <para>RadioButton.Checked fires only for the newly-selected segment, so a
    /// single user pick raises this exactly once.</para></summary>
    private void OnLanguageModeChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;

        UILanguage lang = LanguageEnglish.IsChecked == true
            ? UILanguage.English
            : UILanguage.ChineseTraditional;

        LocalizationManager.Current.Language = lang;
        UserPreferences.Current.LanguageModeRaw = lang.ToString();
        UserPreferences.Current.Save();
    }

    /// <summary>Phase 1.5 user-feedback polish (2026-05-30): the "需重新啟動"
    /// badge became an actionable Button. Click prompts confirmation, then
    /// hands off to a detached <c>cmd /c timeout 2 &amp; start &lt;exe&gt;</c>
    /// process so the current process can release the single-instance mutex
    /// (App.OnExit) before the new instance's
    /// <see cref="SenseEase.App.App.OnStartup"/> probes for the mutex. The
    /// 2-second wait is generous — typical OnExit completes in milliseconds.
    ///
    /// <para>cmd is built-in, no extra deps; <c>CreateNoWindow=true</c> +
    /// <c>WindowStyle=Hidden</c> hides the brief console flash. Failure paths
    /// fall back to a MessageBox so the user is never silently stranded.</para></summary>
    private void OnRestartClicked(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Confirm(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "重啟 SenseEase 以套用變更？",
                    "Restart SenseEase to apply changes?")))
            return;

        try
        {
            // Environment.ProcessPath is the canonical .NET 6+ way to find
            // the host executable; falls back to the main module path if
            // the runtime ever returns null (shouldn't on Windows .NET 8).
            string? exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                ThemedDialog.Notify(
                    Window.GetWindow(this),
                    LocalizationManager.Current.Pick(
                        "無法定位 SenseEase 執行檔，請手動重啟。",
                        "Unable to locate the SenseEase executable; please restart manually."),
                    DialogKind.Warning);
                return;
            }

            // cmd handoff: wait 2 seconds (during which Application.Shutdown
            // → App.OnExit releases the single-instance mutex), then launch
            // a fresh instance. start "" "<path>" is the canonical cmd
            // idiom for spawning detached.
            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exePath}\"",
                CreateNoWindow  = true,
                UseShellExecute = false,
                WindowStyle     = ProcessWindowStyle.Hidden,
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ThemedDialog.Notify(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    $"重啟失敗：{ex.Message}",
                    $"Restart failed: {ex.Message}"),
                DialogKind.Error);
        }
    }

    /// <summary>Alpha-2 addendum: the vestibular "regret" toggle changed. Suppressed during
    /// hydration (<see cref="_wiring"/>). Routes the new value to the host
    /// (MainViewModel.VestibularProtectionEnabled), which drives + persists the engine.</summary>
    private void OnVestibularProtectionChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        SetVestibularProtection?.Invoke(VestibularProtectionToggle.IsChecked == true);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Alpha-2 addendum: the moved-into-Settings "reset personalisation state"
    /// button. Confirms (it is less contextual than the console button was), then routes
    /// to the host's ResetStateCommand (Q4.5-safe — preserves the earned habituation offset).</summary>
    private void OnResetStateClicked(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Confirm(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "確定要重設個人化狀態嗎？\n\n這會清除暫時的個人化，但會保留你原始的習慣紀錄。",
                    "Reset personalisation state?\n\nThis clears transient personalisation, " +
                    "but keeps your original habituation record.")))
            return;
        ResetRequested?.Invoke();
    }

    /// <summary>Open the dedicated Event Log window (2026-06-07). Delegates to the
    /// host-wired action; no-op if unwired (the card is hidden in that case).</summary>
    private void OnOpenEventLogClicked(object sender, RoutedEventArgs e) => OpenEventLog?.Invoke();

    /// <summary>D-15 (2026-06-11): wires the existing
    /// <see cref="UserPreferences.ResetToDefaults"/> to a Settings action behind a
    /// ThemedDialog confirm that plainly enumerates everything that resets — theme,
    /// language, the notification / background toggles, and the saved HUD-card position
    /// — and nothing else. (D-14 deleted the old VestibularWarningShown flag, so the
    /// copy must NOT mention it; the live vestibular state is unaffected.)
    /// <para>This is DISTINCT from <see cref="OnClearAllDataClicked"/>: that clears
    /// DATA, this resets PREFERENCES. Q4.5-safe — <c>ResetToDefaults</c> rewrites only
    /// the preferences file and never opens the habituation store, so the earned offset
    /// and graded-exposure progress survive.</para>
    /// <para>After the reset, the theme and language are re-applied to the live session
    /// immediately (ResetToDefaults fires <see cref="UserPreferences.Changed"/> which
    /// re-hydrates the visible toggles via <see cref="OnPreferencesChangedExternally"/>,
    /// but window chrome theme + the {loc:T} language bindings are driven by their own
    /// managers, so those two are nudged explicitly here).</para></summary>
    private void OnResetPreferencesClicked(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Confirm(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "確定要重設偏好設定嗎？\n\n" +
                    "將還原成預設值：外觀（自動）、語言（繁體中文）、通知與背景開關，以及通知卡片的位置。\n" +
                    "不會刪除你的資料，也不會影響漸進適應進度。\n\n此動作無法復原。",
                    "Reset preferences?\n\n" +
                    "Will restore to defaults: appearance (Auto), language (Traditional Chinese), " +
                    "the notification & background toggles, and the notification-card position.\n" +
                    "This does not delete your data and does not affect graded-exposure progress.\n\n" +
                    "This cannot be undone.")))
            return;

        // Rewrite the preferences file back to defaults (public + static; fires
        // UserPreferences.Changed → our OnPreferencesChangedExternally re-hydrates
        // the visible toggles/radios). Q4.5-safe: touches only the prefs file.
        UserPreferences.ResetToDefaults();

        // Theme + language are applied by their own managers, not by the toggle
        // re-hydration (which runs under _wiring and so does NOT call SetMode /
        // set LocalizationManager.Language). Apply the reset values explicitly so
        // the live window chrome + every {loc:T} binding reflect the reset at once.
        ThemeManager.SetMode((ThemeManager.ThemeMode)UserPreferences.Current.ThemeMode);
        LocalizationManager.Current.Language =
            LocalizationManager.Parse(UserPreferences.Current.LanguageModeRaw);

        // Belt-and-braces re-hydration of THIS surface's controls (the Changed
        // event already did this for every subscribed surface; calling it here too
        // is idempotent under _wiring and keeps the reset deterministic even if a
        // future change reorders the event plumbing).
        RefreshFromPreferences();

        // Let the host repaint dependent surfaces (bar / tray) for the reset toggles.
        PreferencesChanged?.Invoke(this, EventArgs.Empty);

        ThemedDialog.Notify(
            Window.GetWindow(this),
            LocalizationManager.Current.Pick(
                "偏好設定已重設為預設值。",
                "Preferences have been reset to defaults."),
            DialogKind.Info);
    }

    private void OnClearAllDataClicked(object sender, RoutedEventArgs e)
    {
        // UX-09 + FEAT-13 (H24 intent = a COMPLETE clear): the confirm dialog now
        // plainly ENUMERATES everything that will be deleted (behaviour log, analysis
        // index, longitudinal observations, model diagnostic trace, chat transcript,
        // legacy calibration history) AND what is PRESERVED (graded-exposure progress —
        // the Q4.5 offset — and your preferences), so the "Clear all my data" label no
        // longer overstates what the button does.
        if (!ThemedDialog.Confirm(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "確定要清除 SenseEase 為你建立的所有資料並重設嗎？\n\n" +
                    "將刪除：行為紀錄、分析索引、長期觀察、模型診斷追蹤、對話紀錄、感受紀錄、舊版校正紀錄。\n" +
                    "會保留：漸進適應進度與偏好設定。\n\n此動作無法復原。",
                    "Clear all the data SenseEase built about you and reset?\n\n" +
                    "Will delete: behaviour log, analysis index, longitudinal observations, " +
                    "model diagnostic trace, chat transcript, check-in history, legacy calibration history.\n" +
                    "Will keep: your graded-exposure progress and preferences.\n\n" +
                    "This cannot be undone."),
                DialogKind.Warning))
            return;

        try
        {
            int stillLocked = ClearAllUserData();

            ThemedDialog.Notify(
                Window.GetWindow(this),
                stillLocked == 0
                    ? LocalizationManager.Current.Pick(
                        "已清除所有使用者資料。",
                        "All user data cleared.")
                    : LocalizationManager.Current.Pick(
                        $"已清除大部分資料，但有 {stillLocked} 個檔案仍在使用中（可能是防毒掃描），暫時無法移除。請稍候再試一次。",
                        $"Cleared most of your data, but {stillLocked} file(s) were still in use (possibly an antivirus scan) and couldn't be removed. Please try again in a moment."),
                stillLocked == 0 ? DialogKind.Info : DialogKind.Warning);
        }
        catch (Exception ex)
        {
            ThemedDialog.Notify(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    $"清除失敗：{ex.Message}",
                    $"Clear failed: {ex.Message}"),
                DialogKind.Error);
        }
    }

    /// <summary>UX-09 + FEAT-13 (H24 "clear all my data" = a COMPLETE clear): the
    /// dialog-free deletion core, factored out so the B1b tests can drive it against a
    /// temp DataRoot (no live WPF dialog). Deletes every content-bearing user-data file
    /// the label promises:
    /// <list type="bullet">
    ///   <item><c>ClearUserModel()</c> — behaviour log + analysis index (→ zero state) +
    ///         longitudinal observations (truncated; structured deltas preserved).</item>
    ///   <item>the model diagnostic trace (<c>ClearOnDiskTrace</c> target), best-effort.</item>
    ///   <item><c>chat_history.jsonl</c> — the chat transcript (PII), best-effort.</item>
    ///   <item><c>calibration_history.jsonl</c> — the legacy calibration history (PII,
    ///         orphaned from pre-0.11.3 builds), best-effort (FEAT-13).</item>
    /// </list>
    /// Paths come from <see cref="AppConfig"/> singletons, which route through the
    /// <c>DataRoot</c> test seam so the suite isolates off the developer's real data.
    /// <para><b>Q4.5 invariant.</b> This path NEVER touches the habituation offset:
    /// <c>habituation.json</c> (<see cref="AppConfig.HabituationStatePath"/>) and the
    /// habituation profile's deltas/engine-state are left intact — <c>ClearUserModel</c>
    /// only truncates observation memory and zeroes the index, and the two file deletes
    /// added here are unrelated transcript files. Graded-exposure progress and
    /// preferences therefore survive a full clear.</para></summary>
    internal static int ClearAllUserData()
    {
        int failed = 0;   // content files still locked after retries — drives the honest partial-result message

        // H24 step 4: ClearUserModel() (behaviour log + index + observations,
        // structured deltas preserved — Q4.5-safe).
        try
        {
            var profileStore = new FileHabituationProfileStore(AppConfig.HabituationProfilePath);
            var userModel = new UserModelIndexStore(
                AppConfig.UserModelIndexPath, AppConfig.BehaviorLogPath, profileStore);
            userModel.ClearUserModel();
        }
        catch { failed++; }

        // ... + the ClearOnDiskTrace() target (the model trace), best-effort.
        if (!BestEffortDelete(AppConfig.ModelTracePath)) failed++;

        // UX-09 + FEAT-13: the two designated content-bearing PII transcript files the
        // old clear path skipped — now included so the "all my data" label is honest.
        if (!BestEffortDelete(AppConfig.ChatHistoryPath)) failed++;
        if (!BestEffortDelete(AppConfig.CalibrationHistoryPath)) failed++;

        // Beta-1 Phase-1: the structured symptom/comfort event log (FMS self-reports +
        // daily estimates) — DeletableOnClear=true on every row, so the H24 "all my data"
        // clear must remove it too.
        if (!BestEffortDelete(AppConfig.SymptomLogPath)) failed++;

        // Chatbot-tuning sprint (2026-06-19): the dedicated in-chat FMS quick-rate dev log.
        if (!BestEffortDelete(AppConfig.FmsLogPath)) failed++;

        // W-E (2026-06-20): the overlay-consent decline cooldown is BEHAVIOURAL data, not a kept
        // preference — clear it (the asked/accepted/declined/ignored rows in symptom_log are deleted
        // above) so a post-wipe state re-asks normally. Theme/language stay intact: this is the
        // data-clear path, not the preferences-reset path.
        try
        {
            UserPreferences.Current.LastOverlayDeclineUtc = null;
            UserPreferences.Current.OverlayDeclineCount = 0;
            UserPreferences.Current.Save();
        }
        catch { /* best-effort — a prefs write failure must not abort the data clear */ }

        return failed;
    }

    private const int ClearRetries = 3;          // the Trend Micro AV lock is transient (~400ms worst) — retry briefly
    private const int ClearRetryDelayMs = 150;

    /// <summary>Best-effort PII file delete with a short retry for the known TRANSIENT AV lock. Returns
    /// true if the file is gone (deleted or never existed), false if it is STILL locked after the retries —
    /// so <see cref="ClearAllUserData"/> reports an honest partial result instead of a blanket "all cleared".</summary>
    private static bool BestEffortDelete(string path)
    {
        for (int attempt = 0; attempt <= ClearRetries; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;                                 // deleted, or nothing to delete
            }
            catch
            {
                if (attempt >= ClearRetries) return false;   // still in use after the retries — report it
                try { System.Threading.Thread.Sleep(ClearRetryDelayMs); } catch { /* ignore */ }
            }
        }
        return false;
    }
}
