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
        ToggleAuto.IsChecked  = UserPreferences.Current.AutoExpandNotifications;
        ToggleColor.IsChecked = UserPreferences.Current.BarTrayColorEnabled;
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

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
