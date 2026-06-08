using System;
using System.Windows;
using System.Windows.Input;
using SenseEase.App.Theme;

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

        // Dark-mode fix (2026-06-09): keep this window's OWN chrome (Canvas background +
        // Accent Close button, declared with theme tokens in XAML) light/dark-correct,
        // including a live OS theme flip while the dialog is open. Mirrors EventLogWindow;
        // the hosted SettingsContent tracks itself separately.
        Loaded += (_, _) => ThemeManager.TrackElement(this);
        Closed += (_, _) => ThemeManager.UntrackElement(this);
    }

    /// <summary>Alpha-2 addendum: optional host wiring for the auto-profiling Settings
    /// group (vestibular "regret" toggle + moved Reset). Forward to the hosted
    /// <see cref="SettingsContent"/>. Set by the host AFTER construction (before Show) when
    /// it has the MainViewModel; left unset on paths that don't (the group then stays hidden).
    /// Constructor signature is preserved verbatim so existing call sites are unaffected.</summary>
    public Func<bool>? GetVestibularProtection
    {
        get => ContentHost.GetVestibularProtection;
        set => ContentHost.GetVestibularProtection = value;
    }
    public Action<bool>? SetVestibularProtection
    {
        get => ContentHost.SetVestibularProtection;
        set => ContentHost.SetVestibularProtection = value;
    }
    public Action? ResetRequested
    {
        get => ContentHost.ResetRequested;
        set => ContentHost.ResetRequested = value;
    }
    /// <summary>Pass-through for the "open Event Log window" action (2026-06-07).
    /// Wired by the host that owns the MainViewModel (MainWindow); left unset on
    /// paths that don't (the Event Log card then stays hidden).</summary>
    public Action? OpenEventLog
    {
        get => ContentHost.OpenEventLog;
        set => ContentHost.OpenEventLog = value;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>C2 (audit): Escape closes the settings dialog — the standard
    /// cancel/back affordance Apple HIG and WCAG require on a modal surface,
    /// mirroring the "關閉 / Close" button.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }
}
