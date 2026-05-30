using System;
using System.Windows;

namespace SenseEase.App.Views;

/// <summary>
/// Topmost settings dialog opened from the tray menu and the Pop-up Card's
/// gear icon. As of UI Consolidation Sprint M-Phase-1 (2026-05-30) this is
/// thin chrome around the shared <see cref="SettingsContent"/> UserControl —
/// all toggle UI + handlers + persistence logic live in the UserControl, so
/// this Window and the main console's "設定" tab share state automatically
/// through the static <see cref="Configuration.UserPreferences.Current"/> +
/// <c>AppConfig.*</c> singletons.
///
/// <para>Cross-instance visual sync is handled by
/// <see cref="Configuration.UserPreferences.Changed"/> — when this Window is
/// open simultaneously with the main console's Settings tab, a toggle
/// flipped on one surface re-hydrates the other automatically.</para>
///
/// <para>The Constructor signature (<see cref="Func{T}"/>) is preserved
/// verbatim so the existing call sites (<c>MainWindow.ShowSettingsWindow</c>,
/// <c>NotificationCardWindow.OnSettingsClicked</c>) continue to work without
/// modification.</para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Forwards <see cref="SettingsContent.PreferencesChanged"/> from
    /// the hosted UserControl. The host subscribers (bar/tray repaint in
    /// MainWindow, host-callback in NotificationCardWindow) consume this
    /// event verbatim.</summary>
    public event EventHandler? PreferencesChanged;

    public SettingsWindow(Func<bool> hasVestibularDiagnosis)
    {
        InitializeComponent();
        ContentHost.HasVestibularDiagnosis  = hasVestibularDiagnosis ?? (() => false);
        ContentHost.PreferencesChanged     += (s, e) => PreferencesChanged?.Invoke(this, e);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
