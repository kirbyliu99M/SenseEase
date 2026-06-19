using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
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
/// <para>Localization: the tray menu is fully bilingual via
/// <see cref="LocalizationManager.Pick"/> — every visible string (status band
/// label, Open Dashboard / Settings / Quit items, power-save and fault verdicts,
/// tooltip) re-picks on a live EN / 繁體中文 flip through
/// <see cref="RefreshLanguageDependentLabels"/>. (Historical: the menu was zh-only
/// until the i18n service landed; UX-04 closed the last hardcoded-zh strays.)</para>
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
    private readonly ContextMenu _contextMenu;
    private readonly Func<bool> _isBarVisible;
    private readonly Action<bool> _setBarVisible;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _toggleBarItem;
    // UX-04: promoted from ctor locals to fields so RefreshLanguageDependentLabels
    // can re-pick their headers on a live EN / 繁體中文 flip — the menu's other items
    // already re-pick; these two were the last hardcoded-zh strays.
    private readonly MenuItem _settingsItem;
    private readonly MenuItem _quitItem;
    private bool _disposed;
    // Tray-flash fix (Kirby 2026-06-20 r3): true while the context menu is open → ApplyState suppresses the
    // tray-icon/label refresh (re-issuing Shell_NotifyIcon NIM_MODIFY under an open menu flashes/dismisses
    // it). _lastTrayBand de-dupes the icon write so NIM_MODIFY fires ONLY on a real band change, not every tick.
    private bool _menuOpen;
    private TierColorPalette.TierBand? _lastTrayBand;
    private string? _lastTooltip; // de-dupes the _tray.ToolTipText NIM_MODIFY write (identical per band)
    // overlay-bar-redesign diag-4: visibility watchdog. Logs Visibility +
    // IconHandle every 5s so a post-ctor flip to Hidden or a handle change
    // is captured in wgc_diag.txt without needing to re-instrument.
    private DispatcherTimer? _visibilityWatchdog;

    // M12 (TrayDistinct): per-band cache of the locally code-drawn tray icon.
    // Keyed by TierColorPalette.TierBand so two tiers in the same band reuse
    // one System.Drawing.Icon (and one HICON) — so we don't allocate / leak an
    // HICON every StateChanged tick. Released in Dispose().
    private readonly System.Collections.Generic.Dictionary<TierColorPalette.TierBand, System.Drawing.Icon> _distinctIconCache = new();

    // Single/double click disambiguation (2026-06-08): a single tray click opens the
    // pop-up card, a double click the main console. A double-click also raises a first
    // single up-event, so the single action is DEFERRED by this timer and only fires
    // (LeftClicked) if no double-click cancels it within the window (~ default DCT).
    private readonly System.Windows.Threading.DispatcherTimer _clickDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(260) };

    /// <summary>Raised when the user single-left-clicks the tray icon.
    /// Task 3 has no card to expand, so this event has no subscribers in
    /// Task 3 wiring. Task 4 will subscribe and open the notification card.</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Raised on a tray-icon DOUBLE left-click (2026-06-08). Host opens the
    /// main console. Single vs double is disambiguated by <see cref="_clickDebounce"/>:
    /// a single click fires <see cref="LeftClicked"/> only if no double-click follows.</summary>
    public event EventHandler? DoubleClicked;

    /// <summary>Raised when the user clicks the Settings menu item. Task 5's
    /// settings panel will subscribe; for now the host (MainWindow) can
    /// fall back to opening the dashboard.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user clicks the status / pause-resume menu item — the host toggles
    /// power-save (pause ⇄ resume protection). 2026-06-18: replaces the old display-only status line
    /// (which read "tap to resume" while disabled) + the removed "Open Dashboard" item.</summary>
    public event EventHandler? PauseResumeRequested;

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
        // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
        SenseEase.Infrastructure.Real.WgcDiagLog.Append(
            $"[{DateTime.UtcNow:O}] [TrayCtor] entry");

        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _isBarVisible  = isBarVisible  ?? throw new ArgumentNullException(nameof(isBarVisible));
        _setBarVisible = setBarVisible ?? throw new ArgumentNullException(nameof(setBarVisible));

        // Status + pause/resume in one item: shows the live status AND toggles power-save when
        // clicked (honest now — it used to read "tap to resume" while DISABLED, i.e. untappable).
        _statusItem    = new MenuItem();
        _statusItem.Click += (_, _) => PauseResumeRequested?.Invoke(this, EventArgs.Empty);
        _toggleBarItem = new MenuItem();
        _toggleBarItem.Click += (_, _) =>
        {
            bool nowVisible = !_isBarVisible();
            _setBarVisible(nowVisible);
            RefreshToggleBarLabel();
        };

        // "Open Dashboard" tray item REMOVED 2026-06-18 (user: leave only status/pause-resume,
        // settings, close). The console is still reachable by double-clicking the tray icon.

        // UX-04: both headers now localized via LocalizationManager.Pick and re-picked
        // in RefreshLanguageDependentLabels — no more hardcoded-zh menu strays.
        _settingsItem = new MenuItem { Header = LocalizationManager.Current.Pick("設定", "Settings") };
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _quitItem = new MenuItem { Header = LocalizationManager.Current.Pick("退出 SenseEase", "Quit SenseEase") };
        _quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        // Only surface the "toggle bar" item when the persistent-bar feature is on.
        // With AppConfig.PersistentBarEnabled=false (default) the bar is never created,
        // so the item would be a no-op that reads as a broken menu entry — hide it.
        if (SenseEase.App.Configuration.AppConfig.PersistentBarEnabled)
            menu.Items.Add(_toggleBarItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_quitItem);

        // UI/UX (2026-06-09): de-nativise the tray menu. It's a WPF ContextMenu, so skin it
        // to the app's look (rounded Surface card, token colours, calm SurfaceMuted highlight)
        // instead of the default native Windows menu. A popup isn't parented to a themed
        // window, so merge the theme into the menu's OWN resources for the tokens to resolve;
        // ThemeManager.TrackElement then keeps it light/dark-correct (incl. a live OS flip).
        // Degrade-not-crash (tray invariant): any failure leaves the default functional menu.
        _contextMenu = menu;
        try
        {
            menu.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SenseEase.App;component/Theme/SenseEaseTheme.xaml", UriKind.Absolute)
            });
            menu.Style = (Style)menu.FindResource("SenseEaseContextMenu");
            var menuItemStyle = (Style)menu.FindResource("SenseEaseMenuItem");
            var menuSepStyle  = (Style)menu.FindResource("SenseEaseMenuSeparator");
            foreach (var entry in menu.Items)
            {
                if (entry is MenuItem mi) mi.Style = menuItemStyle;
                else if (entry is Separator sep) sep.Style = menuSepStyle;
            }
            SenseEase.App.Theme.ThemeManager.TrackElement(menu);
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [Tray] menu theming failed (left default menu): {ex.GetType().Name}: {ex.Message}");
        }

        // Tray-flash fix (Kirby 2026-06-20 r3): while the menu is OPEN, suppress the per-tick tray-icon
        // refresh — re-issuing Shell_NotifyIcon NIM_MODIFY under an open context menu makes it flash /
        // dismiss (root cause; ApplyState re-set the icon on EVERY engine tick). _menuOpen gates ApplyState;
        // the next tick after Closed resyncs.
        menu.Opened += (_, _) => { _menuOpen = true;  SenseEase.Infrastructure.Real.WgcDiagLog.Append($"[{DateTime.UtcNow:O}] [TrayMenu] Opened"); };
        menu.Closed += (_, _) => { _menuOpen = false; SenseEase.Infrastructure.Real.WgcDiagLog.Append($"[{DateTime.UtcNow:O}] [TrayMenu] Closed"); };

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
            //
            // M12 (TrayDistinct): first paint uses the same shape-cued local
            // builder ApplyState uses, so the initial icon matches what the first
            // StateChanged tick would draw (no flat-then-shaped flicker on launch).
            // D-10 (2026-06-11): the BarTrayColorEnabled suppressor was retired —
            // the tile is a constant indigo lens and only the small accent dot
            // tracks the live tier (calm-safe by design), so the initial dot
            // reflects the live VimsTier directly.
            Icon        = BuildDistinctTrayIcon(TierColorPalette.BandForTier(_vm.VimsTier)),
        };
        // Single click → deferred LeftClicked (card); double click → cancel + DoubleClicked (console).
        _clickDebounce.Tick += (_, _) =>
        {
            _clickDebounce.Stop();
            LeftClicked?.Invoke(this, EventArgs.Empty);
        };
        _tray.TrayLeftMouseUp     += (_, _) => { _clickDebounce.Stop(); _clickDebounce.Start(); };
        _tray.TrayMouseDoubleClick += (_, _) =>
        {
            _clickDebounce.Stop();
            DoubleClicked?.Invoke(this, EventArgs.Empty);
        };

        // Initial labels reflect current state; refreshed on every state change.
        RefreshLabels(_vm.CurrentState);

        // Subscribe AFTER the tray is fully constructed so an early-tick
        // StateChanged doesn't try to touch _statusItem before it's wired.
        _vm.StateChanged += OnStateChanged;

        // Phase 5 Step 3: self-subscribe to UserPreferences.Changed so that
        // ANY preference writer (SettingsContent toggles, UserPreferences
        // .ResetToDefaults, future chatbot intents) repaints the tray icon —
        // not just the SettingsContent path that the host wires explicitly
        // via RefreshAppearance(). Static event ⇒ MUST -= in Dispose or the
        // service is pinned for the process lifetime.
        UserPreferences.Changed += OnPreferencesChanged;

        // Live EN / 繁體中文 toggle: when the language flips, re-pick every
        // language-dependent tray string (status band label, Open Dashboard
        // header, toggle-bar header, tooltip) so the menu updates without a
        // restart. INotifyPropertyChanged ⇒ MUST -= in Dispose or the service
        // is pinned for the process lifetime by the singleton's invocation list.
        LocalizationManager.Current.PropertyChanged += OnLanguageChanged;

        // overlay-bar-redesign diag: single-line marker confirming TaskbarIcon
        // construction reached completion AND surfacing key state (Icon.Handle,
        // Visibility) so a user-reported "tray completely missing" can be
        // distinguished between (a) Win11 overflow hiding the icon,
        // (b) H.NotifyIcon silent registration failure, (c) TrayIconService
        // never constructed at all — without re-running the app.
        // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
        SenseEase.Infrastructure.Real.WgcDiagLog.Append(
            $"[{DateTime.UtcNow:O}] [Tray] TaskbarIcon initialized, " +
            $"Icon.Handle=0x{_tray.Icon?.Handle:X}, " +
            $"Visible={_tray.Visibility}");

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
            // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [TrayWatchdog] Visibility={_tray.Visibility}, " +
                $"IconHandle=0x{_tray.Icon?.Handle.ToInt64():X}");
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

    // Phase 5 Step 3: handler for UserPreferences.Changed. The static event
    // fires on whatever thread called Save() (per its XML doc) — today that's
    // always the UI thread (toggle Checked/Unchecked), but ResetToDefaults
    // and future chatbot intents may fire from a worker. Mirror the
    // OnStateChanged marshal shape and read _vm.CurrentState fresh (the event
    // carries no payload). Defensive try/catch: UserPreferences.Save raises
    // Changed without try/catch, so an exception here would propagate into
    // the toggle handler in SettingsContent and crash the settings flow.
    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            if (_tray.Dispatcher.CheckAccess())
            {
                ApplyState(_vm.CurrentState);
            }
            else
            {
                _tray.Dispatcher.BeginInvoke(new Action(() => ApplyState(_vm.CurrentState)));
            }
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [Tray] OnPreferencesChanged threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Live EN / 繁體中文 toggle handler. Marshals to the tray's dispatcher and
    // re-picks every language-dependent string via RefreshLanguageDependentLabels
    // (status band label, Open Dashboard / Settings / Quit headers, toggle-bar
    // header, tooltip, and the bilingual power-save + fault indicators) so the menu
    // follows the toggle without a restart.
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is not (nameof(LocalizationManager.Language) or nameof(LocalizationManager.IsEnglish)))
            return;
        try
        {
            if (_tray.Dispatcher.CheckAccess())
                RefreshLanguageDependentLabels();
            else
                _tray.Dispatcher.BeginInvoke(new Action(RefreshLanguageDependentLabels));
        }
        catch (Exception ex)
        {
            SenseEase.Infrastructure.Real.WgcDiagLog.Append(
                $"[{DateTime.UtcNow:O}] [Tray] OnLanguageChanged threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Re-applies every language-dependent tray string for the CURRENT state. The
    // band-label status + toggle-bar header + tooltip flow through RefreshLabels;
    // the Open Dashboard / Settings / Quit headers, the power-save header, and the
    // fault indicator strings are re-picked here because they live outside
    // RefreshLabels (UX-04 closed the formerly-hardcoded-zh strays).
    private void RefreshLanguageDependentLabels()
    {
        // UX-04: re-pick the formerly-hardcoded-zh menu items on a language flip, like the others.
        _settingsItem.Header = LocalizationManager.Current.Pick("設定", "Settings");
        _quitItem.Header     = LocalizationManager.Current.Pick("退出 SenseEase", "Quit SenseEase");

        // UX-04: re-pick the power-save header (formerly zh-only "已暫停 · 點此恢復")
        // in the active language. While power save is ON it lives in the visible
        // _statusItem.Header; while OFF its stash is null and ApplyState owns the
        // header — so only rewrite the visible header when we're actually in PS.
        if (_preSavedStatusText != null)
            _statusItem.Header = PowerSaveStatusText(LocalizationManager.Current.IsEnglish);

        // Refresh the band-label status + toggle-bar header + tooltip. RefreshLabels
        // is now fault-aware: when a fault is showing it refreshes the STASH and
        // leaves the visible header/tooltip alone (INTENT-05), so the fault override
        // below is NOT clobbered. It still refreshes the power-save snapshot so a
        // later SetPowerSaveIndicator(false) restores the band label in the active
        // language.
        RefreshLabels(_vm.CurrentState);

        // Re-pick the fault indicator's visible strings in the new language. The copy
        // is the honest automatic-recovery verdict (UX-03 / CW-8) — never "paused",
        // never a fake "tap to retry". RefreshLabels above no longer touches these
        // while a fault is active, but we re-pick here so the language flip lands.
        if (_preFaultStatusText != null)
        {
            _statusItem.Header = FaultStatusText(LocalizationManager.Current.IsEnglish);
            _tray.ToolTipText  = FaultToolTipText(LocalizationManager.Current.IsEnglish);
        }
    }

    private void ApplyState(SenseState state)
    {
        // Tray-flash fix (Kirby 2026-06-20 r3): while the context menu is OPEN, don't touch the tray at all.
        // Re-issuing Shell_NotifyIcon NIM_MODIFY (the per-tick icon/tooltip refresh — a vestige of the OLD
        // per-tier icon TONE change; the new icon is a constant indigo lens) under an open menu flashes /
        // dismisses it. The next tick after Closed resyncs.
        if (_menuOpen) return;

        // Fix 3: use the System.Drawing.Icon path (TaskbarIcon.Icon), NOT the
        // broken ImageSource path (TaskbarIcon.IconSource). See
        // BuildDistinctTrayIcon below for HICON lifecycle details.
        //
        // Alpha-1 (TrayBrand): the icon is a CONSISTENT brand mark — a fixed
        // indigo (#4B6EF5) tile + white lens — with the tier conveyed SUBTLY by
        // a small bottom-right accent dot (BuildDistinctTrayIcon → PaintBand).
        // The whole tile is never darkened/recoloured, so the 16 px tray slot
        // always reads as "the SenseEase indigo lens".
        //
        // D-10 (2026-06-11): the BarTrayColorEnabled suppressor was retired. It
        // previously forced the calm/VeryHigh band (tier 1) when the toggle was
        // OFF; with the Alpha-1 indigo-lens icon the tile is already constant and
        // only the subtle, calm-safe accent dot varies by tier, so there is no
        // alarm surface to suppress. The dot now always tracks the live VimsTier.
        // De-dupe the NIM_MODIFY: write the icon ONLY on a real band change. The icon is now a constant
        // indigo lens whose only per-band variation is a subtle accent dot (5 coarse bands), so re-issuing
        // it every tick was pure churn left over from the old per-tier tone change.
        var band = TierColorPalette.BandForTier(state.VimsTier);
        if (band != _lastTrayBand)
        {
            _tray.Icon = BuildDistinctTrayIcon(band);
            _lastTrayBand = band;
        }
        RefreshLabels(state);
    }

    /// <summary>The live tier-band comfort label for a state (極高/高/中/低/極低 ·
    /// Most Natural…Most Steady) in the active language — the SAME comfort label as
    /// the Dashboard hero, single-sourced from TierColorPalette.FeelLabel(En). Idle
    /// (tier ≤ 0, engine not yet producing a level) → 準備中 / Getting ready. Factored
    /// out so the fault-clear tooltip restore and RefreshLabels share one source.</summary>
    private static string CurrentStatusLabel(SenseState state)
    {
        // We intentionally do NOT pull the habituation profile (RESILIENT / TYPICAL /
        // SENSITIVE) here even though it would be informative — IInferenceEngine
        // doesn't expose it and reaching into the concrete MockInferenceEngine would
        // couple the tray to the engine impl. The chat tab is the place for that
        // detail. Tier alone is enough for an ambient indicator.
        if (state.VimsTier <= 0)
            return LocalizationManager.Current.Pick("準備中", "Getting ready");
        var band = TierColorPalette.BandForTier(state.VimsTier);
        return LocalizationManager.Current.Pick(
            TierColorPalette.FeelLabel(band), TierColorPalette.FeelLabelEn(band));
    }

    private void RefreshLabels(SenseState state)
    {
        string header = CurrentStatusLabel(state);

        // INTENT-05 / CW-8: during self-heal warmup, StateChanged keeps ticking and
        // this method runs ~every 0.6 s. If a fault is showing, an UNCONDITIONAL
        // write here would clobber the fault verdict back to a healthy band label —
        // the exact "looks fine, then a fault, then fine again" flicker the audit
        // flagged. Mirror the power-save guard below: when the fault stash is active,
        // refresh the STASH (so the post-fault restore lands the current tier) and
        // skip BOTH the visible-header AND the tooltip overwrite. The fault treatment
        // then persists until SetFaultIndicator(false) genuinely clears it. The
        // decision is the pure ShouldPreserveFaultVerdict predicate so the unit test
        // exercises the SAME branch the live path takes.
        if (ShouldPreserveFaultVerdict(_preFaultStatusText != null))
        {
            _preFaultStatusText = header;
            RefreshToggleBarLabel();
            return;
        }

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
        // immediate read; the menu label carries the PS state. De-dupe the write (r3): _tray.ToolTipText=
        // is a Shell_NotifyIcon NIM_MODIFY, and `header` is identical for every tick within a band, so
        // skip the redundant write to kill the steady-state ~15 Hz churn (mirrors the _lastTrayBand guard).
        string tip = $"SenseEase — {header}";  // same comfort label as the Dashboard hero
        if (tip != _lastTooltip)
        {
            _tray.ToolTipText = tip;
            _lastTooltip = tip;
        }

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
            _statusItem.Header = PowerSaveStatusText(LocalizationManager.Current.IsEnglish);
        }
        else if (_preSavedStatusText != null)
        {
            _statusItem.Header = _preSavedStatusText;
            _preSavedStatusText = null;
        }
    }

    /// <summary>UX-03 / UX-04: the DELIBERATE-user-pause status copy. "Paused" /
    /// 已暫停 wording is EXCLUSIVE to this power-save path — the orthogonal fault
    /// layer (<see cref="FaultStatusText"/>) never says "paused", so the two states
    /// can never be confused. "點此恢復 / tap to resume" is honest here: a click on
    /// the bar/tray genuinely resumes from power save (unlike the old fault "tap to
    /// retry", which retried nothing). Internal for the same WPF-free test seam as
    /// the fault copy.</summary>
    internal static string PowerSaveStatusText(bool isEnglish)
        => isEnglish ? "Paused by you — tap to resume" : "已暫停 · 點此恢復";

    // D3 liveness contract: snapshot of the pre-fault status label so clearing
    // the fault restores the exact tier-band string the menu was showing. Non-null
    // only while the fault indicator is ON; independent of the power-save snapshot.
    private string? _preFaultStatusText;

    // UX-03 fault treatment that is NOT colour-only. The tray tile is a fixed brand
    // indigo at every band (it never recolours), so the fault read can't rely on a
    // colour swap. Instead the status item text + tooltip both carry a warning glyph
    // PREFIX paired with the honest reconnecting label — a shape+text differentiator
    // that survives greyscale / colour-blind viewing. The glyph (⚠) is a status cue,
    // never the only cue: the words "偵測中斷…" / "Sensing interrupted…" stand alone.
    internal const string FaultGlyph = "⚠"; // ⚠ warning sign

    /// <summary>UX-03 / CW-8: the honest fault status-item copy. Recovery is
    /// AUTOMATIC via WgcSensingProvider, so the wording is "reconnecting
    /// automatically" — NOT a fake "tap to retry" that retries nothing, and NOT
    /// "paused" (that wording stays EXCLUSIVE to the deliberate user power-save
    /// path, <see cref="SetPowerSaveIndicator"/>). Internal + InternalsVisibleTo so
    /// a test can assert the copy is reconnecting-honest and paused-free without
    /// standing up a WPF dispatcher.</summary>
    internal static string FaultStatusText(bool isEnglish)
        => FaultGlyph + " " + (isEnglish
            ? "Sensing interrupted — reconnecting automatically"
            : "偵測中斷，正在自動重新連線");

    /// <summary>INTENT-05 / CW-8 guard, as a pure predicate so it is unit-testable
    /// without a WPF dispatcher. <c>true</c> ⇒ <see cref="RefreshLabels"/> must
    /// refresh the FAULT STASH and skip the visible header + tooltip writes, so the
    /// fault verdict survives the per-tick band-label refresh during self-heal
    /// warmup. <c>false</c> ⇒ the normal (power-save-aware) write path runs. Mirrors
    /// the language path's documented work-around for the same clobber.</summary>
    internal static bool ShouldPreserveFaultVerdict(bool faultStashActive) => faultStashActive;

    /// <summary>UX-03 / CW-8: the honest fault OS-tooltip copy. The "SenseEase — "
    /// brand prefix mirrors the healthy tooltip shape; the glyph + reconnecting
    /// verdict make Faulted unmistakable from Paused at a glance. Internal for the
    /// same test seam as <see cref="FaultStatusText"/>.</summary>
    internal static string FaultToolTipText(bool isEnglish) => "SenseEase — "
        + FaultGlyph + " " + (isEnglish
            ? "Sensing interrupted — reconnecting automatically"
            : "偵測中斷，正在自動重新連線");

    /// <summary>D3 / UX-03: tray-side Faulted indicator, mirroring
    /// <see cref="SetPowerSaveIndicator"/>. When set, the status header + OS
    /// tooltip become the honest, automatic-recovery verdict
    /// (<see cref="FaultStatusText"/> / <see cref="FaultToolTipText"/>) — calm,
    /// never "ERROR"/red, never "paused", and carrying a warning-glyph prefix so
    /// the fault read is shape+text, not colour-only (the indigo tile never
    /// recolours). The prior label is stashed. When cleared, the prior label is
    /// restored; the next <see cref="ApplyState"/> tick (frames resume on recovery)
    /// repaints the live tooltip. The fault treatment PERSISTS across the per-tick
    /// <see cref="RefreshLabels"/> writes during self-heal warmup (INTENT-05) —
    /// those refresh the stash, not the visible header/tooltip — until this method
    /// is called with <c>false</c> or the underlying liveness genuinely clears.
    /// Idempotent across repeated true / repeated false.</summary>
    public void SetFaultIndicator(bool faulted)
    {
        if (_statusItem is null) return;
        if (faulted)
        {
            _preFaultStatusText ??= _statusItem.Header as string;
            _statusItem.Header = FaultStatusText(LocalizationManager.Current.IsEnglish);
            _tray.ToolTipText  = FaultToolTipText(LocalizationManager.Current.IsEnglish);
        }
        else if (_preFaultStatusText != null)
        {
            _statusItem.Header = _preFaultStatusText;
            _preFaultStatusText = null;
            // Repaint the live tooltip now that the fault verdict is cleared. The
            // next ApplyState tick also does this, but on recovery frames may not
            // resume for a beat — restore immediately so the OS tooltip stops
            // claiming a fault the moment SetFaultIndicator(false) lands.
            _tray.ToolTipText = $"SenseEase — {CurrentStatusLabel(_vm.CurrentState)}";
        }
    }

    // ── Alpha-1 (TrayBrand): one consistent indigo "SenseEase lens" icon ─────
    //
    // The previous tray icon painted the WHOLE tile in the band hue (grey /
    // amber / clay) and layered a hatch / wedge on top. At the 16 px tray slot
    // the calm/VeryHigh band's dark grey tile read as a broken dark square, and
    // the icon's identity changed band-to-band — it never settled into a
    // recognizable "this is SenseEase" mark.
    //
    // Fix: the tile is now ALWAYS the brand indigo (#4B6EF5) with a white lens
    // centred on it — the exact, consistent shape of the indigo app icon. The
    // band/tier is conveyed SUBTLY by a small accent dot in the bottom-right
    // corner (calm bands = cool/quiet, elevated = warm) — never by darkening or
    // recolouring the whole tile. So at any size the primary read is "the
    // SenseEase indigo lens", and the dot is a secondary, glanceable status cue.
    //
    // The multi-size ICO machinery is unchanged: render NATIVE frames at
    // {16,24,32,48} px and pack them into one ICO (BuildMultiSizeIco —
    // uncompressed 32-bit BGRA DIB frames; GDI+ has no ICO encoder so we
    // hand-write the container) so the Win11 shell picks the exact-size frame
    // instead of down-scaling a single 32 px one. Reduced motion is N/A: tray
    // icons are static (spec §1 "never animates").

    // Brand indigo — the FIXED tile background at every band, matching the app
    // icon (#4B6EF5). The band hue is NEVER applied to the tile; only the small
    // accent dot below varies, so the indigo lens identity is constant.
    private static readonly System.Drawing.Color TrayIndigoFill = System.Drawing.Color.FromArgb(255, 0x4B, 0x6E, 0xF5); // #4B6EF5 brand indigo

    // Per-band accent-dot hues. These tint ONLY the small bottom-right status
    // dot, not the tile. Cool/quiet for the calm bands, warming gently toward
    // the most-stabilized band — calm-audience safe (no alarm red). Fully
    // opaque so the dot survives the 16 px downscale.
    private static readonly System.Drawing.Color TrayDotVeryHigh = System.Drawing.Color.FromArgb(255, 0xCF, 0xDD, 0xFA); // 極高 — airy near-white blue (most natural)
    private static readonly System.Drawing.Color TrayDotHigh     = System.Drawing.Color.FromArgb(255, 0x8F, 0xB0, 0xF2); // 高   — light indigo-blue
    private static readonly System.Drawing.Color TrayDotMedium   = System.Drawing.Color.FromArgb(255, 0xF2, 0xC9, 0x6B); // 中   — soft warm amber
    private static readonly System.Drawing.Color TrayDotLow      = System.Drawing.Color.FromArgb(255, 0xE8, 0xA9, 0x77); // 低   — muted warm clay-amber
    private static readonly System.Drawing.Color TrayDotVeryLow  = System.Drawing.Color.FromArgb(255, 0xD8, 0x8C, 0x7A); // 極低 — muted clay (never alarm red)

    /// <summary>Alpha-1 (TrayBrand): returns the consistent indigo-lens tray icon
    /// for a band (fixed indigo tile + white lens + a subtle per-band accent
    /// dot), cached so repeated same-band ticks reuse one <see
    /// cref="System.Drawing.Icon"/> (and HICON). Released in <see
    /// cref="Dispose"/>. The icon is a multi-size ICO (16/24/32/48 px frames via
    /// <see cref="BuildMultiSizeIco"/>) so the Win11 shell renders a native
    /// 16 px frame in the tray instead of down-scaling a single 32 px one.</summary>
    private System.Drawing.Icon BuildDistinctTrayIcon(TierColorPalette.TierBand band)
    {
        if (_distinctIconCache.TryGetValue(band, out var cached)) return cached;

        byte[] icoBytes = BuildBandIco(band);
        // Icon(Stream) reads the stream eagerly in the ctor, so disposing the
        // MemoryStream right after is safe — matches the prior TierColorPalette
        // path. The Icon owns the HICON it allocates; we Dispose() it in Dispose.
        using var stream = new MemoryStream(icoBytes);
        var icon = new System.Drawing.Icon(stream);
        _distinctIconCache[band] = icon;
        return icon;
    }

    /// <summary>Renders the band at every shell-relevant size and packs the
    /// frames into one multi-size ICO. The previous path wrote a SINGLE 32 px
    /// PNG frame; the Win11 shell then down-scaled that 32 px PNG to the 16 px
    /// tray slot and produced a blurred / blank icon (the same "PNG-at-16px"
    /// bug the app icon hit). Shipping a native 16 px frame (plus 24/32/48) lets
    /// the shell pick the exact-size frame instead of resampling one.</summary>
    private static byte[] BuildBandIco(TierColorPalette.TierBand band)
    {
        // {16,24,32,48}: 16 = primary tray slot, 24 = 150% DPI tray, 32 =
        // 200% DPI / overflow flyout, 48 = jumbo / high-DPI fallback. PaintBand
        // simplifies the lens below 24 px so the ring/highlight don't vanish.
        int[] sizes = { 16, 24, 32, 48 };
        var frames = new System.Collections.Generic.List<System.Drawing.Bitmap>(sizes.Length);
        try
        {
            foreach (int size in sizes)
            {
                var bmp = new System.Drawing.Bitmap(size, size);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(System.Drawing.Color.Transparent);
                    // A proportional ~6% transparent margin so the tile never
                    // touches the shell's icon bounding box at any size.
                    int margin = System.Math.Max(1, size / 16);
                    var body = new System.Drawing.Rectangle(
                        margin, margin, size - 2 * margin, size - 2 * margin);
                    PaintBand(g, body, band);
                }
                frames.Add(bmp);
            }
            return BuildMultiSizeIco(frames);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
    }

    /// <summary>Internal test hook: returns the raw multi-size ICO bytes for a
    /// band so TrayIconResourceTests can assert frame count &gt; 1 and the
    /// presence of a 16×16 / 32-bit ICONDIRENTRY — without round-tripping
    /// through Win32's icon serialiser (which may normalise). Internal +
    /// InternalsVisibleTo("SenseEase.Tests") (already wired in the csproj).</summary>
    internal static byte[] GetBandIcoBytesForTesting(TierColorPalette.TierBand band)
        => BuildBandIco(band);

    /// <summary>Paints one tray frame: the FIXED indigo "app tile", the SenseEase
    /// white brand lens centred on it, and a SUBTLE per-band accent dot in the
    /// bottom-right corner — all scaled to <paramref name="body"/>. The tile is
    /// always brand indigo (#4B6EF5), never the band hue, so the primary read at
    /// every size is "the SenseEase indigo lens". The lens DETAIL scales with
    /// size: at ≥24 px the full lens (translucent disc + ring + top-left
    /// highlight) is drawn; at 16–20 px the thin ring and highlight collapse to
    /// sub-pixel noise after the shell blends them, so only a simpler filled
    /// disc is drawn. The accent dot is the only band-varying element; it is
    /// small enough that it never overpowers the indigo lens, yet glanceable as
    /// a tier cue.</summary>
    private static void PaintBand(
        System.Drawing.Graphics g,
        System.Drawing.Rectangle body,
        TierColorPalette.TierBand band)
    {
        // Corner radius proportional to the body so the tile keeps the same
        // "app tile" silhouette at every size (≈19% of the body width — the
        // 6 px radius on the old 28 px body).
        int radius = System.Math.Max(2, body.Width * 6 / 28);

        // Fixed indigo tile — identical at every band. The tier is conveyed by
        // the accent dot below, NOT by recolouring the tile.
        using (var tile = RoundedRectPath(body, radius))
        using (var fillBrush = new System.Drawing.SolidBrush(TrayIndigoFill))
            g.FillPath(fillBrush, tile);

        // Brand lens. At ≥24 px draw the full lens (ring + highlight); at the
        // tiny 16–20 px tray sizes those thin features vanish, so draw a single
        // filled disc on the indigo tile instead.
        if (body.Width >= 24)
            DrawBrandLens(g, body);
        else
            DrawSimpleBrandDisc(g, body);

        // Subtle per-band accent dot in the bottom-right corner. Drawn LAST so
        // it sits above the lens edge; sized small (≈22% of the body) so the
        // indigo lens stays the dominant read at the 16 px tray slot.
        DrawBandAccentDot(g, body, AccentDotColor(band));
    }

    /// <summary>Per-band accent-dot colour. The only band-varying element of the
    /// tray icon — the tile + lens are identical across bands.</summary>
    private static System.Drawing.Color AccentDotColor(TierColorPalette.TierBand band) => band switch
    {
        TierColorPalette.TierBand.VeryHigh => TrayDotVeryHigh,
        TierColorPalette.TierBand.High     => TrayDotHigh,
        TierColorPalette.TierBand.Medium   => TrayDotMedium,
        TierColorPalette.TierBand.Low      => TrayDotLow,
        TierColorPalette.TierBand.VeryLow  => TrayDotVeryLow,
        _                                  => TrayDotVeryHigh,
    };

    /// <summary>Rounded-rectangle path for the tray tile body.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(
        System.Drawing.Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Paints the SenseEase brand lens — a near-opaque white disc with a
    /// crisp white ring and a top-left highlight — centred on the indigo tray
    /// tile, echoing the AppIcon's lens. On the fixed indigo fill this reads as a
    /// clean white lens (the brand mark), so the tray icon stays recognizable as
    /// SenseEase at every band.</summary>
    private static void DrawBrandLens(System.Drawing.Graphics g, System.Drawing.Rectangle body)
    {
        // Lens diameter ≈57% of the body (16 px on the old 28 px body), scaled
        // so the mark keeps its proportions at 24 / 32 / 48 px frames.
        float dia = body.Width * 16f / 28f;
        float cx = body.X + body.Width / 2f;
        float cy = body.Y + body.Height / 2f;
        var lens = new System.Drawing.RectangleF(cx - dia / 2f, cy - dia / 2f, dia, dia);
        // Near-opaque white disc for a clean, high-contrast lens on indigo. A
        // hair of translucency (240) keeps a soft edge against the indigo rather
        // than a hard cut-out.
        using (var disc = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(240, 255, 255, 255)))
            g.FillEllipse(disc, lens);
        float ringWidth = System.Math.Max(1f, dia / 10f);
        using (var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 255, 255, 255), ringWidth))
            g.DrawEllipse(ring, lens);
        // Soft top-left highlight tinted faint indigo so the lens reads as a
        // glassy surface over the brand tile rather than a flat white dot.
        float hiDia = dia * 3.4f / 16f;
        float hiInset = dia * 2.6f / 16f;
        using (var hi = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 0xEE, 0xF2, 0xFF)))
            g.FillEllipse(hi, cx - dia / 2f + hiInset, cy - dia / 2f + hiInset, hiDia, hiDia);
    }

    /// <summary>Simplified brand mark for the tiny 16–20 px tray frames: a
    /// single near-opaque white disc (no thin ring, no 3.4 px highlight — both
    /// collapse to sub-pixel noise at 16 px after the shell blends them). On the
    /// indigo tile this still reads as a clean white lens with a recognizable
    /// round silhouette.</summary>
    private static void DrawSimpleBrandDisc(System.Drawing.Graphics g, System.Drawing.Rectangle body)
    {
        float dia = body.Width * 16f / 28f;
        float cx = body.X + body.Width / 2f;
        float cy = body.Y + body.Height / 2f;
        var lens = new System.Drawing.RectangleF(cx - dia / 2f, cy - dia / 2f, dia, dia);
        using var disc = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(245, 255, 255, 255));
        g.FillEllipse(disc, lens);
    }

    /// <summary>Alpha-1 (TrayBrand): the SUBTLE per-band tier cue — a small
    /// filled accent dot in the bottom-right corner of the indigo tile, with a
    /// thin indigo "halo" stroke so it sits cleanly on the tile edge without a
    /// muddy seam. Sized ≈22% of the body so it never competes with the white
    /// lens for the primary read; the dot still survives the 16 px downscale
    /// because it is a solid, fully-opaque blob (no thin strokes inside).</summary>
    private static void DrawBandAccentDot(System.Drawing.Graphics g, System.Drawing.Rectangle body, System.Drawing.Color dot)
    {
        float d = System.Math.Max(3f, body.Width * 6f / 28f); // ≈22% of body
        // Inset from the bottom-right so the dot tucks just inside the rounded
        // corner rather than overhanging the tile edge.
        float inset = System.Math.Max(1f, body.Width * 1.5f / 28f);
        float x = body.Right - inset - d;
        float y = body.Bottom - inset - d;
        var rect = new System.Drawing.RectangleF(x, y, d, d);
        // Thin indigo separator halo (a slightly darker indigo) so the dot reads
        // as a distinct chip on the indigo tile even when its own hue is a light
        // indigo-blue (calm bands).
        float haloW = System.Math.Max(0.75f, d / 6f);
        using (var halo = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 0x2F, 0x4B, 0xC2), haloW))
            g.DrawEllipse(halo, x - haloW / 2f, y - haloW / 2f, d + haloW, d + haloW);
        using (var brush = new System.Drawing.SolidBrush(dot))
            g.FillEllipse(brush, rect);
    }

    /// <summary>Builds a proper MULTI-SIZE ICO from the supplied frames, each
    /// encoded as an UNCOMPRESSED 32-bit BGRA DIB (BITMAPINFOHEADER, no PNG).
    /// <para>This replaces the old single-32px-PNG wrapper. That wrapper shipped
    /// exactly one 32 px frame, so the Win11 shell had nothing to do but
    /// resample it down to the 16 px tray slot — producing the blurred / blank
    /// tray icon. Packing native 16 / 24 / 32 / 48 px frames lets the shell pick
    /// the exact-size frame for the slot instead of down-scaling one.</para>
    /// <para>Layout: ICONDIR(reserved=0, type=1, count=N) + N×ICONDIRENTRY
    /// (16 bytes each) + N payloads. Each payload is a DIB: a
    /// BITMAPINFOHEADER whose <c>biHeight = 2*height</c> (the doubled height is
    /// the ICO convention covering the XOR colour rows plus the AND mask), then
    /// the bottom-up 32-bit BGRA XOR rows, then an all-zero 1-bpp AND mask whose
    /// rows are padded to a 4-byte boundary. We hand-write the container because
    /// GDI+ on .NET 8 has no ICO encoder.</para></summary>
    private static byte[] BuildMultiSizeIco(
        System.Collections.Generic.IReadOnlyList<System.Drawing.Bitmap> frames)
    {
        if (frames == null) throw new ArgumentNullException(nameof(frames));
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));

        // Encode each frame's DIB payload up front so we know the byte sizes
        // before writing the ICONDIRENTRY offsets.
        var payloads = new byte[frames.Count][];
        for (int i = 0; i < frames.Count; i++)
            payloads[i] = BuildBgraDibPayload(frames[i]);

        // Offset of the first payload = ICONDIR (6) + N × ICONDIRENTRY (16).
        int headerSize = 6 + frames.Count * 16;

        using var icoStream = new MemoryStream();
        using (var writer = new BinaryWriter(icoStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ── ICONDIR (6 bytes) ──
            writer.Write((ushort)0);                 // Reserved
            writer.Write((ushort)1);                 // Type = 1 (ICO)
            writer.Write((ushort)frames.Count);      // ImageCount = N

            // ── N × ICONDIRENTRY (16 bytes each) ──
            int offset = headerSize;
            for (int i = 0; i < frames.Count; i++)
            {
                int w = frames[i].Width;
                int h = frames[i].Height;
                writer.Write((byte)(w >= 256 ? 0 : w)); // Width  (BYTE; 0 = 256)
                writer.Write((byte)(h >= 256 ? 0 : h)); // Height (BYTE; 0 = 256)
                writer.Write((byte)0);                  // ColorCount: 0 for ≥256 colours
                writer.Write((byte)0);                  // Reserved
                writer.Write((ushort)1);                // Planes
                writer.Write((ushort)32);               // BitCount: 32 (BGRA)
                writer.Write((uint)payloads[i].Length); // bytesInRes
                writer.Write((uint)offset);             // imageOffset
                offset += payloads[i].Length;
            }
        }
        // ── N payloads, in the same order as the entries ──
        foreach (var payload in payloads)
            icoStream.Write(payload, 0, payload.Length);
        return icoStream.ToArray();
    }

    /// <summary>Encodes one frame as an uncompressed 32-bit BGRA DIB for ICO
    /// embedding: a BITMAPINFOHEADER with <c>biHeight = 2*height</c>, then the
    /// bottom-up BGRA XOR rows, then an all-zero 1-bpp AND mask (rows padded to
    /// 4 bytes). The doubled <c>biHeight</c> is the ICO convention — it spans
    /// the colour rows AND the mask; readers halve it back to the real height.
    /// The 32-bit alpha channel carries real transparency, so the AND mask is
    /// all-zero (fully opaque where alpha decides) and present only to satisfy
    /// the format.</summary>
    private static byte[] BuildBgraDibPayload(System.Drawing.Bitmap bmp)
    {
        int width  = bmp.Width;
        int height = bmp.Height;

        // XOR rows: 32-bit BGRA, already 4-byte aligned (4 bytes/pixel).
        int xorRowBytes = width * 4;
        // AND mask rows: 1 bit/pixel, each row padded up to a 4-byte boundary.
        int andRowBytes = ((width + 31) / 32) * 4;

        var data = new System.Drawing.Imaging.BitmapData();
        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        // Lock as 32bppArgb so we read premultiplied-free straight BGRA bytes
        // in memory order B,G,R,A — exactly the DIB pixel layout.
        var locked = bmp.LockBits(
            rect,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb,
            data);

        byte[] xorTopDown = new byte[height * xorRowBytes];
        try
        {
            for (int y = 0; y < height; y++)
            {
                IntPtr src = locked.Scan0 + y * locked.Stride;
                System.Runtime.InteropServices.Marshal.Copy(
                    src, xorTopDown, y * xorRowBytes, xorRowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(locked);
        }

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ── BITMAPINFOHEADER (40 bytes) ──
            writer.Write((uint)40);            // biSize
            writer.Write(width);               // biWidth
            writer.Write(height * 2);          // biHeight = 2*height (XOR + AND)
            writer.Write((ushort)1);           // biPlanes
            writer.Write((ushort)32);          // biBitCount
            writer.Write((uint)0);             // biCompression = BI_RGB
            writer.Write((uint)(xorRowBytes * height)); // biSizeImage (colour rows)
            writer.Write(0);                   // biXPelsPerMeter
            writer.Write(0);                   // biYPelsPerMeter
            writer.Write((uint)0);             // biClrUsed
            writer.Write((uint)0);             // biClrImportant

            // ── XOR colour data: bottom-up rows ──
            for (int y = height - 1; y >= 0; y--)
                writer.Write(xorTopDown, y * xorRowBytes, xorRowBytes);

            // ── AND mask: all-zero, 1bpp, rows padded to 4 bytes ──
            byte[] andZeroRow = new byte[andRowBytes];
            for (int y = 0; y < height; y++)
                writer.Write(andZeroRow, 0, andRowBytes);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _clickDebounce.Stop(); } catch { }
        // diag-4: stop the visibility watchdog so its DispatcherTimer +
        // closure don't pin the tray service after disposal.
        try { _visibilityWatchdog?.Stop(); } catch { }
        _vm.StateChanged -= OnStateChanged;
        // Phase 5 Step 3: static event — MUST unsubscribe or the tray
        // service (and its transitive graph: VM, TaskbarIcon, ContextMenu,
        // MenuItems, watchdog timer, cached HICONs) is pinned for the
        // process lifetime by the static invocation list.
        UserPreferences.Changed -= OnPreferencesChanged;
        // Live-language toggle: singleton INotifyPropertyChanged — MUST -= or the
        // service (and its transitive graph) is pinned for the process lifetime.
        LocalizationManager.Current.PropertyChanged -= OnLanguageChanged;
        // UI/UX: stop tracking the tray menu's light/dark theme. Weak-ref inside, but pair
        // Track with Untrack on teardown per the ThemeManager contract. Best-effort.
        try { SenseEase.App.Theme.ThemeManager.UntrackElement(_contextMenu); } catch { }
        try { _tray.Dispose(); } catch { /* best-effort; shell unregister may race */ }
        // M12 (TrayDistinct): release the locally code-drawn band icons. The
        // Icon(Stream) ctor takes ownership of the HICON it allocates, so
        // Icon.Dispose() fully releases each one — no manual DestroyIcon. Done
        // AFTER _tray.Dispose() so the live icon's handle isn't pulled while
        // the shell still references it. Safe even if _tray.Dispose() already
        // dropped the shell-side reference (HICONs are OS ref-counted).
        foreach (var icon in _distinctIconCache.Values)
        {
            try { icon.Dispose(); } catch { /* best-effort */ }
        }
        _distinctIconCache.Clear();
    }
}
