using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using SenseEase.App.Configuration;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Models;

namespace SenseEase.App.Services;

/// <summary>
/// Pop-up sprint Task 3: Windows system tray icon for SenseEase. Runs in
/// parallel with <see cref="PersistentBarWindow"/> — both surface the current
/// VIMS tier via the shared <see cref="TierColorPalette"/> (no copy-pasted
/// colour table). They are independent UI surfaces — users can hide one and
/// keep the other; turning both off plus closing the bar window is the
/// "fully silent UI" mode that still leaves the engine running.
///
/// <para>Per spec §1, the tray icon is always present, always tier-coloured,
/// and never animates. Left-click opens the notification card (wired in
/// Task 4 — Task 3 fires the <see cref="LeftClicked"/> event but the host
/// has no card to expand yet). Right-click shows a 4-item context menu:
/// current status / toggle bar visibility / settings / quit.</para>
///
/// <para>Localization: the tray menu is currently zh-only. The app has no
/// localization service; spinning one up just for the tray is scope creep
/// (per Task 3 guidance 2026-05-26). When an i18n service lands in a future
/// sprint, replace the literal strings with resource lookups here. The TODO
/// is logged in docs/POPUP-OVERLAY.md (Task 7).</para>
///
/// <para>Disposal: caller (MainWindow) owns the lifetime and calls
/// <see cref="Dispose"/> on app close. The underlying
/// <see cref="TaskbarIcon"/> unregisters from the Windows shell deterministically
/// so the icon doesn't ghost in the tray after exit.</para>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly TaskbarIcon _tray;
    private readonly Func<bool> _isBarVisible;
    private readonly Action<bool> _setBarVisible;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _toggleBarItem;
    private bool _disposed;

    /// <summary>Raised when the user single-left-clicks the tray icon.
    /// Task 3 has no card to expand, so this event has no subscribers in
    /// Task 3 wiring. Task 4 will subscribe and open the notification card.</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Raised when the user clicks the Settings menu item. Task 5's
    /// settings panel will subscribe; for now the host (MainWindow) can
    /// fall back to opening the dashboard.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user clicks the Quit menu item. Host is
    /// expected to call <c>Application.Current.Shutdown()</c>.</summary>
    public event EventHandler? QuitRequested;

    public TrayIconService(
        MainViewModel vm,
        Func<bool> isBarVisible,
        Action<bool> setBarVisible)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _isBarVisible  = isBarVisible  ?? throw new ArgumentNullException(nameof(isBarVisible));
        _setBarVisible = setBarVisible ?? throw new ArgumentNullException(nameof(setBarVisible));

        _statusItem    = new MenuItem { IsEnabled = false };  // disabled = display-only
        _toggleBarItem = new MenuItem();
        _toggleBarItem.Click += (_, _) =>
        {
            bool nowVisible = !_isBarVisible();
            _setBarVisible(nowVisible);
            RefreshToggleBarLabel();
        };

        var settingsItem = new MenuItem { Header = "設定" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var quitItem = new MenuItem { Header = "退出 SenseEase" };
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_toggleBarItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "SenseEase",
            ContextMenu = menu,
            // Fix 3: System.Drawing.Icon path via TaskbarIcon.Icon — bypasses
            // the WPF-ImageSource converter that crashed twice (Crash 1:
            // NotImplementedException on RenderTargetBitmap; Crash 2:
            // NullReferenceException because in-memory BitmapImage has no
            // UriSource for the library's URI-based pixel loader). The Icon
            // property routes straight to Win32 Shell_NotifyIcon's HICON
            // requirement — what the system shell actually needs.
            Icon        = TierColorPalette.IconForTier_Win(_vm.VimsTier),
        };
        _tray.TrayLeftMouseUp += (_, _) => LeftClicked?.Invoke(this, EventArgs.Empty);

        // Initial labels reflect current state; refreshed on every state change.
        RefreshLabels(_vm.CurrentState);

        // Subscribe AFTER the tray is fully constructed so an early-tick
        // StateChanged doesn't try to touch _statusItem before it's wired.
        _vm.StateChanged += OnStateChanged;

        // overlay-bar-redesign diag: single-line marker confirming TaskbarIcon
        // construction reached completion AND surfacing key state (Icon.Handle,
        // Visibility) so a user-reported "tray completely missing" can be
        // distinguished between (a) Win11 overflow hiding the icon,
        // (b) H.NotifyIcon silent registration failure, (c) TrayIconService
        // never constructed at all — without re-running the app. Best-effort
        // IO; a write failure here must NOT break tray init.
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "wgc_diag.txt"),
                $"[{DateTime.UtcNow:O}] [Tray] TaskbarIcon initialized, " +
                $"Icon.Handle=0x{_tray.Icon?.Handle:X}, " +
                $"Visible={_tray.Visibility}\n");
        }
        catch { /* diag log is fire-and-forget */ }
    }

    private void OnStateChanged(object? sender, SenseState state)
    {
        // Marshal to UI thread — StateChanged may fire from the engine's
        // dispatcher timer thread. WPF dependency-property writes require the
        // owning Dispatcher.
        if (_tray.Dispatcher.CheckAccess())
        {
            ApplyState(state);
        }
        else
        {
            _tray.Dispatcher.BeginInvoke(new Action(() => ApplyState(state)));
        }
    }

    private void ApplyState(SenseState state)
    {
        // Toggle B (user pref): when colour-on is OFF, force the grey band
        // regardless of tier (per spec §1 — bar/tray "永遠維持灰色透明").
        // Fix 3: use the System.Drawing.Icon path (TaskbarIcon.Icon), NOT
        // the broken ImageSource path (TaskbarIcon.IconSource). See
        // TierColorPalette.IconForTier_Win for HICON lifecycle details.
        int effectiveTier = UserPreferences.Current.BarTrayColorEnabled ? state.VimsTier : 1;
        _tray.Icon = TierColorPalette.IconForTier_Win(effectiveTier);
        RefreshLabels(state);
    }

    private void RefreshLabels(SenseState state)
    {
        // Status menu item shows the current tier band. We intentionally do
        // NOT pull the habituation profile (RESILIENT / TYPICAL / SENSITIVE)
        // here even though it would be informative — IInferenceEngine doesn't
        // expose it and reaching into the concrete MockInferenceEngine would
        // couple the tray to the engine impl. The chat tab is the place for
        // that detail. Tier alone is enough for an ambient status indicator.
        string band = TierColorPalette.BandForTier(state.VimsTier) switch
        {
            TierColorPalette.TierBand.Grey  => "舒緩",
            TierColorPalette.TierBand.Amber => "中度",
            TierColorPalette.TierBand.Red   => "高壓",
            _                               => "—",
        };
        _statusItem.Header = $"VIMS Tier {state.VimsTier} · {band}";

        // Tooltip mirrors the status item so a hover read works without
        // opening the menu (matches Win11 tray UX expectations).
        _tray.ToolTipText = $"SenseEase — Tier {state.VimsTier} · {band}";

        RefreshToggleBarLabel();
    }

    private void RefreshToggleBarLabel()
    {
        _toggleBarItem.Header = _isBarVisible() ? "隱藏 bar" : "顯示 bar";
    }

    /// <summary>Task 5: re-applies icon + labels for the given state.
    /// Called by the host after a Toggle B flip so the tray icon repaints
    /// immediately instead of waiting for the next StateChanged tick.</summary>
    public void RefreshAppearance(SenseState state)
    {
        if (_tray.Dispatcher.CheckAccess()) ApplyState(state);
        else _tray.Dispatcher.BeginInvoke(new Action(() => ApplyState(state)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.StateChanged -= OnStateChanged;
        try { _tray.Dispose(); } catch { /* best-effort; shell unregister may race */ }
        // Fix 3: release the cached HICONs. Bitmap.GetHicon() returned them
        // unmanaged; Icon.FromHandle() doesn't take ownership; without
        // explicit DestroyIcon they leak until process exit. Safe even if
        // _tray.Dispose() already released the shell-side reference (the
        // OS reference-counts HICONs).
        TierColorPalette.DisposeWinIconCache();
    }
}
