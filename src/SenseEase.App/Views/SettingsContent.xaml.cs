using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        // This UserControl merges the LIGHT SenseEaseTheme.xaml into its OWN
        // Resources (for the non-colour StaticResource tokens — radius, elevation,
        // typography). That view-local light dictionary shadows the dark palette
        // ThemeManager merges at the Window level, so we must merge the dark
        // override into OUR OWN resources too. Tracking it applies the current
        // theme now AND re-applies on every later theme change (incl. a live OS
        // light/dark flip in Auto mode). Unregistered in OnUnloaded.
        ThemeManager.TrackElement(this);

        // Event Log card (2026-06-07): only shown where the host wired OpenEventLog
        // (it needs the MainViewModel-backed window). Hidden in the notification
        // card's quick-Settings popup, which has no MainViewModel.
        EventLogCard.Visibility = OpenEventLog is null ? Visibility.Collapsed : Visibility.Visible;

        RefreshFromPreferences();
        UserPreferences.Changed += OnPreferencesChangedExternally;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.UntrackElement(this);
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

            ToggleAuto.IsChecked           = UserPreferences.Current.AutoExpandNotifications;
            ToggleMinimizeToTray.IsChecked = UserPreferences.Current.MinimizeToTrayOnClose;
            // Prefer the registry's actual state for run-at-startup so an
            // externally-edited registry doesn't desync from what we display.
            ToggleRunAtStartup.IsChecked   = AutoStartRegistry.IsEnabled();

            ToggleHeadTrackingEnabled.IsChecked =
                UserPreferences.Current.HeadTrackingEnabled;

            // UX-08: show the Restart-needed chip only on a genuine pending change
            // (toggle ≠ the value the app started with), not on every Settings open.
            UpdateRestartChip();

            // FEAT-08 (B1b UI half): surface the live sidecar health (or hide the
            // row where no reader exists). Read-only consumption of the host reader.
            RefreshHeadTrackingHealth();

            bool isFullFollow = string.Equals(
                UserPreferences.Current.HeadTrackingFollowMode,
                "FullFollow",
                StringComparison.OrdinalIgnoreCase);
            FollowModeBalanced.IsChecked = !isFullFollow;
            FollowModeFull.IsChecked     =  isFullFollow;

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

    private void OnToggleAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        UserPreferences.Current.AutoExpandNotifications = ToggleAuto.IsChecked == true;
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

    private void OnFollowModeChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool isFullFollow = FollowModeFull.IsChecked == true;
        UserPreferences.Current.HeadTrackingFollowMode =
            isFullFollow ? "FullFollow" : "Balanced";
        UserPreferences.Current.Save();
        AppConfig.HeadTrackingFollowMode = isFullFollow
            ? HeadTrackingFollowModeKind.FullFollow
            : HeadTrackingFollowModeKind.Balanced;
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
                    "將刪除：行為紀錄、分析索引、長期觀察、模型診斷追蹤、對話紀錄、舊版校正紀錄。\n" +
                    "會保留：漸進適應進度與偏好設定。\n\n此動作無法復原。",
                    "Clear all the data SenseEase built about you and reset?\n\n" +
                    "Will delete: behaviour log, analysis index, longitudinal observations, " +
                    "model diagnostic trace, chat transcript, legacy calibration history.\n" +
                    "Will keep: your graded-exposure progress and preferences.\n\n" +
                    "This cannot be undone."),
                DialogKind.Warning))
            return;

        try
        {
            ClearAllUserData();

            ThemedDialog.Notify(
                Window.GetWindow(this),
                LocalizationManager.Current.Pick(
                    "已清除所有使用者資料。",
                    "All user data cleared."),
                DialogKind.Info);
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
    internal static void ClearAllUserData()
    {
        // H24 step 4: ClearUserModel() (behaviour log + index + observations,
        // structured deltas preserved — Q4.5-safe).
        var profileStore = new FileHabituationProfileStore(AppConfig.HabituationProfilePath);
        var userModel = new UserModelIndexStore(
            AppConfig.UserModelIndexPath, AppConfig.BehaviorLogPath, profileStore);
        userModel.ClearUserModel();

        // ... + the ClearOnDiskTrace() target (the model trace), best-effort.
        BestEffortDelete(AppConfig.ModelTracePath);

        // UX-09 + FEAT-13: the two designated content-bearing PII transcript files the
        // old clear path skipped — now included so the "all my data" label is honest.
        BestEffortDelete(AppConfig.ChatHistoryPath);
        BestEffortDelete(AppConfig.CalibrationHistoryPath);
    }

    /// <summary>Best-effort PII file delete — a missing file is success (nothing to
    /// clear), and a transient lock (e.g. the known AV lock) is swallowed so one stuck
    /// file can never abort the whole clear. Used by <see cref="ClearAllUserData"/>.</summary>
    private static void BestEffortDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort PII cleanup — never abort the clear on one locked file */ }
    }
}
