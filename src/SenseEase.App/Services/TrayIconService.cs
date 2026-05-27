using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
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
/// and never animates. Left-click opens the notification card. Right-click
/// shows a 4-item context menu: current status / toggle bar visibility /
/// settings / quit.</para>
///
/// <para>Library: <c>Hardcodet.Wpf.TaskbarNotification.TaskbarIcon</c> (Patch 5
/// 2026-05-26). The original H.NotifyIcon.Wpf 2.3.0 exhibited a silent
/// Shell_NotifyIcon registration failure — library internal state reported
/// success (Visibility=Visible + valid HICON for 100+ seconds) but the
/// Win32 shell never received registration (confirmed absent from
/// Settings → Other system tray icons). Both <c>dotnet run</c> and direct
/// <c>.exe</c> execution showed the same failure (Amendment 3 result),
/// eliminating dev-build/manifest hypotheses. Hardcodet.NotifyIcon.Wpf is
/// the de facto WPF tray library used by Microsoft tooling and major OSS
/// projects (Files, parts of PowerToys). API surface used here (Icon,
/// ContextMenu, ToolTipText, Visibility, TrayLeftMouseUp event) is the
/// same shape both libraries expose; the swap is a mechanical
/// namespace-qualifier change.</para>
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
    // overlay-bar-redesign diag-4: visibility watchdog. Logs Visibility +
    // IconHandle every 5s so a post-ctor flip to Hidden or a handle change
    // is captured in wgc_diag.txt without needing to re-instrument.
    private DispatcherTimer? _visibilityWatchdog;

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
        // overlay-bar-redesign diag-2: first line in ctor so any exception
        // throws BEFORE this log line means construction never reached this
        // file. Pairs with the end-of-ctor log from 99ec13d — the gap
        // between entry and exit logs localises where ctor breaks.
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "wgc_diag.txt"),
                $"[{DateTime.UtcNow:O}] [TrayCtor] entry\n");
        }
        catch { /* fire-and-forget */ }

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

        // overlay-bar-redesign diag-4: visibility watchdog. Diag-3 confirmed
        // the ctor reaches its end with Visibility=Visible AND a valid
        // Icon.Handle, yet the user reports the icon is missing from all
        // three OS-level locations (main tray, overflow flyout, Settings →
        // other system tray icons). This timer polls Visibility + IconHandle
        // every 5s so a post-ctor flip — caused by a dispatcher event, a
        // library Visibility/Shell_NotifyIcon desync, or some other race —
        // shows up in wgc_diag.txt as a value change. The user is asked to
        // leave the app running for ~2 minutes to capture ~24 ticks.
        _visibilityWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _visibilityWatchdog.Tick += (_, _) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "wgc_diag.txt"),
                    $"[{DateTime.UtcNow:O}] [TrayWatchdog] Visibility={_tray.Visibility}, " +
                    $"IconHandle=0x{_tray.Icon?.Handle.ToInt64():X}\n");
            }
            catch { /* fire-and-forget */ }
        };
        _visibilityWatchdog.Start();
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
        string header = $"VIMS Tier {state.VimsTier} · {band}";
        // Path Z bug-fix 1: while in Power Save, refresh the stashed snapshot
        // instead of the visible "Power Save ON" header. Otherwise a tier
        // change during a PS window overwrites the visible header AND leaves
        // _preSavedStatusText pointing at a stale pre-PS string — so the
        // restore on PS OFF shows the wrong tier band.
        if (_preSavedStatusText != null)
            _preSavedStatusText = header;
        else
            _statusItem.Header = header;

        // Tooltip mirrors the live tier always — the OS tooltip is the
        // immediate read; the menu label carries the PS state.
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

    // overlay-bar-redesign D3: snapshot of the pre-power-save status label
    // so a Power-Save OFF restores the exact tier-band string the menu was
    // showing. Non-null only while power save is ON.
    private string? _preSavedStatusText;

    /// <summary>overlay-bar-redesign D3: tray-side Power Save indicator.
    /// When enabled, replaces the status menu item's header with
    /// "Power Save ON" and stashes the prior label; when disabled, restores
    /// the prior label so the next <see cref="ApplyState"/> can repaint it
    /// from the live state. Idempotent across repeated true / repeated
    /// false calls. Tray invariant: this is the ONLY new public method on
    /// the service for D3.</summary>
    public void SetPowerSaveIndicator(bool enabled)
    {
        if (_statusItem is null) return;
        if (enabled)
        {
            _preSavedStatusText ??= _statusItem.Header as string;
            _statusItem.Header = "Power Save ON";
        }
        else if (_preSavedStatusText != null)
        {
            _statusItem.Header = _preSavedStatusText;
            _preSavedStatusText = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // diag-4: stop the visibility watchdog so its DispatcherTimer +
        // closure don't pin the tray service after disposal.
        try { _visibilityWatchdog?.Stop(); } catch { }
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
