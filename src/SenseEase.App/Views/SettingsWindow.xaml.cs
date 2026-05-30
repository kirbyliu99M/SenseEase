using System;
using System.Windows;
using SenseEase.App.Configuration;

namespace SenseEase.App.Views;

/// <summary>
/// Pop-up sprint Task 5: settings panel for the two persistent toggles.
/// Modal-feeling (Topmost, no taskbar entry), self-contained. Opened from
/// the tray menu's "設定" item.
///
/// <para>Toggle A (auto-expand notifications): direct save on change.</para>
///
/// <para>Toggle B (bar/tray colour): direct save on change, BUT a Toggle
/// B → OFF transition for a user whose calibration profile shows a
/// vestibular diagnosis pops the single-use safety warning per spec §4.
/// The user can still proceed (we never block their choice); the warning
/// flag is persisted so the second OFF-attempt doesn't re-nag.</para>
///
/// <para>i18n: zh-only (same TODO as tray service — i18n service is out
/// of scope for this sprint).</para>
///
/// <para>M8 head-tracking sprint (2026-05-30): adds the
/// "頭部追蹤 / Head Tracking" section. Mirrors changes onto the static
/// AppConfig.HeadTracking* props for runtime-readable settings (follow
/// mode + escape hatch) and persists everything to UserPreferences for the
/// next launch. The enable toggle is annotated "需重新啟動" because the
/// IHeadPoseReader factory + LightSensorProvider both bind once at
/// MainViewModel construction. Escape hatch is force-disabled for users
/// with a vestibular diagnosis (FOV mask is their VIMS protection;
/// opening a 3-second transparency window could surface symptoms).</para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Func<bool> _hasVestibularDiagnosis;
    private bool _wiring; // suppress save while we initialise the checkboxes

    /// <summary>Raised after any persisted toggle change so the host can
    /// repaint dependent surfaces (bar colour, tray icon) immediately
    /// instead of waiting for the next StateChanged push.</summary>
    public event EventHandler? PreferencesChanged;

    public SettingsWindow(Func<bool> hasVestibularDiagnosis)
    {
        InitializeComponent();
        _hasVestibularDiagnosis = hasVestibularDiagnosis ?? (() => false);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialise from persisted prefs. The _wiring guard prevents the
        // Checked/Unchecked handlers from re-saving during setup (they'd
        // see _wiring=true and bail out).
        _wiring = true;
        ToggleAuto.IsChecked           = UserPreferences.Current.AutoExpandNotifications;
        ToggleColor.IsChecked          = UserPreferences.Current.BarTrayColorEnabled;
        ToggleMinimizeToTray.IsChecked = UserPreferences.Current.MinimizeToTrayOnClose;
        // Path Z UX iter 5: prefer the registry's actual state over the
        // persisted UserPreferences flag, in case the registry was edited
        // externally or a prior write failed. The two are reconciled by
        // OnToggleRunAtStartupChanged on every user interaction.
        ToggleRunAtStartup.IsChecked   = AutoStartRegistry.IsEnabled();

        // M8 head-tracking sprint: hydrate the three new toggles from prefs.
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

        // Escape hatch × vestibular guard (M8 Item 3). When the user's
        // calibration recorded a vestibular diagnosis, the escape hatch is
        // disabled at the UI level AND the OverlayWindow side (defensive
        // layer in case prefs flip without restart). Tooltip explains why
        // so the disabled state isn't perceived as "broken".
        bool vest = _hasVestibularDiagnosis();
        if (vest)
        {
            ToggleEscapeHatch.IsEnabled = false;
            ToggleEscapeHatch.ToolTip =
                "因為偵測到前庭敏感，暫時透明功能已停用以維持視覺保護。\n" +
                "Disabled because vestibular sensitivity detected; temporary " +
                "transparency would compromise visual protection.";
            EscapeHatchVestibularNote.Visibility = Visibility.Visible;
        }

        _wiring = false;
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
            && _hasVestibularDiagnosis()
            && !UserPreferences.Current.VestibularWarningShown)
        {
            var result = MessageBox.Show(
                "你的 calibration 紀錄顯示你曾被診斷過前庭相關狀況。\n\n" +
                "關閉顏色變化會讓系統的即時提示完全靜默。\n" +
                "建議至少保留顏色變化，作為視覺壓力的 ambient signal。",
                "SenseEase 安全提示",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            // Whatever the user picks, mark the warning shown so the
            // second attempt doesn't re-nag (single-warning rule).
            UserPreferences.Current.VestibularWarningShown = true;

            if (result != MessageBoxResult.OK)
            {
                // User cancelled — revert the checkbox visually. Setting
                // IsChecked re-fires this handler, so use _wiring to skip
                // the second pass.
                _wiring = true;
                ToggleColor.IsChecked = true;
                _wiring = false;
                UserPreferences.Current.Save(); // persist the warning-shown flag even on cancel
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
        // Write the registry FIRST. If the OS write fails (permission,
        // policy, antivirus), surface the failure by reverting the checkbox
        // visually rather than silently saying "saved" when it wasn't.
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

    // ── M8 head-tracking sprint handlers ─────────────────────────────────────

    /// <summary>M8: master opt-in for head tracking. Persists to prefs and
    /// to AppConfig (so a subsequent in-session restart sees the new value
    /// without re-reading the file). Effect at runtime requires app restart:
    /// the IHeadPoseReader factory + LightSensorProvider factory both run
    /// once at MainViewModel construction, so flipping this mid-session
    /// does not start or stop the sidecar. The XAML annotation
    /// "需重新啟動 / Requires restart" surfaces this constraint to the user.</summary>
    private void OnToggleHeadTrackingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = ToggleHeadTrackingEnabled.IsChecked == true;
        UserPreferences.Current.HeadTrackingEnabled = desired;
        UserPreferences.Current.Save();
        // Mirror to AppConfig so the in-memory value matches what the next
        // launch will read. The current MainViewModel instance keeps its
        // already-bound reader (whichever variant the factory built) until
        // the app restarts.
        AppConfig.HeadTrackingEnabled = desired;
    }

    /// <summary>M8: follow mode (Balanced / FullFollow). Runtime-changeable:
    /// OverlayWindow.ApplyHeadPoseToBrush reads AppConfig.HeadTrackingFollowMode
    /// per frame, so the change takes effect immediately without restart.
    /// Fires from either RadioButton's Checked event; we read whichever is
    /// currently checked from the group.</summary>
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

    /// <summary>M8: escape-hatch (double-tap Ctrl) opt-in. Runtime-changeable:
    /// MainWindow's PreviewKeyDown handler reads AppConfig.HeadTrackingEscapeHatchEnabled
    /// on each Ctrl tap and OverlayWindow.TriggerEscapeHatch also gates on
    /// it as a defensive second check. For vestibular-diagnosed users the
    /// CheckBox is disabled at load time (see OnLoaded) AND OverlayWindow
    /// no-ops at TriggerEscapeHatch (see Item 3 OverlayWindow change) so
    /// even a forced-on state via JSON edit still preserves the protection.</summary>
    private void OnToggleEscapeHatchChanged(object sender, RoutedEventArgs e)
    {
        if (_wiring) return;
        bool desired = ToggleEscapeHatch.IsChecked == true;
        UserPreferences.Current.HeadTrackingEscapeHatchEnabled = desired;
        UserPreferences.Current.Save();
        AppConfig.HeadTrackingEscapeHatchEnabled = desired;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
