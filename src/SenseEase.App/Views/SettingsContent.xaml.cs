using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SenseEase.App.Configuration;
using SenseEase.App.Theme;

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
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromPreferences();
        UserPreferences.Changed += OnPreferencesChangedExternally;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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

            ToggleAuto.IsChecked           = UserPreferences.Current.AutoExpandNotifications;
            ToggleColor.IsChecked          = UserPreferences.Current.BarTrayColorEnabled;
            ToggleMinimizeToTray.IsChecked = UserPreferences.Current.MinimizeToTrayOnClose;
            // Prefer the registry's actual state for run-at-startup so an
            // externally-edited registry doesn't desync from what we display.
            ToggleRunAtStartup.IsChecked   = AutoStartRegistry.IsEnabled();

            ToggleHeadTrackingEnabled.IsChecked =
                UserPreferences.Current.HeadTrackingEnabled;

            bool isFullFollow = string.Equals(
                UserPreferences.Current.HeadTrackingFollowMode,
                "FullFollow",
                StringComparison.OrdinalIgnoreCase);
            FollowModeBalanced.IsChecked = !isFullFollow;
            FollowModeFull.IsChecked     =  isFullFollow;

            ToggleEscapeHatch.IsChecked =
                UserPreferences.Current.HeadTrackingEscapeHatchEnabled;

            // Escape hatch × vestibular guard (M8 Item 3). Read-only
            // consumption — never writes to Habituation state.
            bool vest = HasVestibularDiagnosis?.Invoke() ?? false;
            if (vest)
            {
                ToggleEscapeHatch.IsEnabled = false;
                ToggleEscapeHatch.ToolTip =
                    "因為偵測到前庭敏感，暫時透明功能已停用以維持視覺保護。\n" +
                    "Disabled because vestibular sensitivity detected; temporary " +
                    "transparency would compromise visual protection.";
                EscapeHatchVestibularNote.Visibility = Visibility.Visible;
            }
            else
            {
                ToggleEscapeHatch.IsEnabled = true;
                ToggleEscapeHatch.ToolTip   = null;
                EscapeHatchVestibularNote.Visibility = Visibility.Collapsed;
            }
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

    private void OnToggleColorChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool newValue = ToggleColor.IsChecked == true;
        bool wasOn    = UserPreferences.Current.BarTrayColorEnabled;

        // Vestibular safety warning: only fires on a true → false transition,
        // only if user has a vestibular diagnosis on file, only if the
        // warning hasn't been shown before. Single-warning rule per spec §4.
        if (wasOn && !newValue
            && (HasVestibularDiagnosis?.Invoke() ?? false)
            && !UserPreferences.Current.VestibularWarningShown)
        {
            var result = MessageBox.Show(
                "你的 calibration 紀錄顯示你曾被診斷過前庭相關狀況。\n\n" +
                "關閉顏色變化會讓系統的即時提示完全靜默。\n" +
                "建議至少保留顏色變化，作為視覺壓力的 ambient signal。",
                "SenseEase 安全提示",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            UserPreferences.Current.VestibularWarningShown = true;

            if (result != MessageBoxResult.OK)
            {
                _wiring = true;
                ToggleColor.IsChecked = true;
                _wiring = false;
                UserPreferences.Current.Save(); // persist warning-shown flag even on cancel
                return;
            }
        }

        UserPreferences.Current.BarTrayColorEnabled = newValue;
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
            MessageBox.Show(
                "無法寫入登入啟動設定。可能的原因：\n" +
                "- 系統管理員或安全性軟體限制了登入啟動項目。\n" +
                "- 註冊表權限不足。\n\n" +
                "你可以手動把 SenseEase 加到 Windows「啟動」資料夾作為替代方案。",
                "SenseEase 設定", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void OnToggleEscapeHatchChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = ToggleEscapeHatch.IsChecked == true;
        UserPreferences.Current.HeadTrackingEscapeHatchEnabled = desired;
        UserPreferences.Current.Save();
        AppConfig.HeadTrackingEscapeHatchEnabled = desired;
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
        var result = MessageBox.Show(
            "重啟 SenseEase 以套用變更？\n\nRestart SenseEase to apply changes?",
            "SenseEase",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.OK) return;

        try
        {
            // Environment.ProcessPath is the canonical .NET 6+ way to find
            // the host executable; falls back to the main module path if
            // the runtime ever returns null (shouldn't on Windows .NET 8).
            string? exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                MessageBox.Show(
                    "無法定位 SenseEase 執行檔，請手動重啟。\n\n" +
                    "Unable to locate the SenseEase executable; please restart manually.",
                    "SenseEase", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(
                $"重啟失敗：{ex.Message}\n\nRestart failed: {ex.Message}",
                "SenseEase", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
