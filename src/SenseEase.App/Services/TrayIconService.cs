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

    // M12 (TrayDistinct): per-band cache of the locally code-drawn tray icon.
    // Keyed by TierColorPalette.TierBand so two tiers in the same band reuse
    // one System.Drawing.Icon (and one HICON) — matching the old
    // TierColorPalette._winIconCache behaviour so we don't allocate / leak an
    // HICON every StateChanged tick. Released in Dispose().
    private readonly System.Collections.Generic.Dictionary<TierColorPalette.TierBand, System.Drawing.Icon> _distinctIconCache = new();

    /// <summary>Raised when the user single-left-clicks the tray icon.
    /// Task 3 has no card to expand, so this event has no subscribers in
    /// Task 3 wiring. Task 4 will subscribe and open the notification card.</summary>
    public event EventHandler? LeftClicked;

    /// <summary>Raised when the user clicks the Settings menu item. Task 5's
    /// settings panel will subscribe; for now the host (MainWindow) can
    /// fall back to opening the dashboard.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Path Z UX iter 5: raised when the user clicks the "Open
    /// Dashboard" tray menu item. Host surfaces MainWindow — this is the
    /// primary way back to the dashboard once the user has hidden it via
    /// the close button (which now minimizes to tray by default).</summary>
    public event EventHandler? OpenDashboardRequested;

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

        _statusItem    = new MenuItem { IsEnabled = false };  // disabled = display-only
        _toggleBarItem = new MenuItem();
        _toggleBarItem.Click += (_, _) =>
        {
            bool nowVisible = !_isBarVisible();
            _setBarVisible(nowVisible);
            RefreshToggleBarLabel();
        };

        // Path Z UX iter 5: explicit "Open Dashboard" entry. Once the user
        // closes the MainWindow (which now minimizes to tray by default),
        // the tray icon is the only path back to the dashboard. The
        // notification card's Open Dashboard button works too, but a
        // direct tray-menu route is the canonical pattern users expect
        // from tray-resident apps.
        var openDashboardItem = new MenuItem { Header = "顯示主視窗 / Open Dashboard" };
        openDashboardItem.Click += (_, _) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new MenuItem { Header = "設定" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var quitItem = new MenuItem { Header = "退出 SenseEase" };
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openDashboardItem);
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
            //
            // M12 (TrayDistinct): first paint uses the same shape-cued local
            // builder ApplyState uses, honouring the BarTrayColorEnabled toggle
            // so the initial icon matches what the first StateChanged tick would
            // draw (no flat-then-shaped flicker on launch).
            Icon        = BuildDistinctTrayIcon(TierColorPalette.BandForTier(
                              UserPreferences.Current.BarTrayColorEnabled ? _vm.VimsTier : 1)),
        };
        _tray.TrayLeftMouseUp += (_, _) => LeftClicked?.Invoke(this, EventArgs.Empty);

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

    private void ApplyState(SenseState state)
    {
        // Toggle B (user pref): when colour-on is OFF, force the grey band
        // regardless of tier (per spec §1 — bar/tray "永遠維持灰色透明").
        // Fix 3: use the System.Drawing.Icon path (TaskbarIcon.Icon), NOT
        // the broken ImageSource path (TaskbarIcon.IconSource). See
        // BuildDistinctTrayIcon below for HICON lifecycle details.
        //
        // M12 (TrayDistinct): the grey↔amber icons were only ~35% colour
        // distance and blurred at the 16 px shell size. We now draw the icon
        // locally (BuildDistinctTrayIcon) so the amber/elevated band is set
        // apart from grey by SHAPE (a diagonal hatch), not hue alone —
        // colour-blind-safe — and the amber hue itself is bumped to a more
        // saturated tone. The contract of this method is unchanged: resolve
        // the effective band, assign _tray.Icon, refresh the labels.
        int effectiveTier = UserPreferences.Current.BarTrayColorEnabled ? state.VimsTier : 1;
        _tray.Icon = BuildDistinctTrayIcon(TierColorPalette.BandForTier(effectiveTier));
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
        // 5級 動態感 label via the single-source helper — never hardcode 級 strings.
        string feel = TierColorPalette.FeelLabel(TierColorPalette.BandForTier(state.VimsTier));
        string header = $"動態感 {feel}";  // plain-language only — the tier index is advanced info (Dashboard-only)
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
        _tray.ToolTipText = $"SenseEase — 動態感 {feel}";  // plain status; no tier index in the OS tooltip

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
            _statusItem.Header = "已暫停 · 點此恢復";
        }
        else if (_preSavedStatusText != null)
        {
            _statusItem.Header = _preSavedStatusText;
            _preSavedStatusText = null;
        }
    }

    // D3 liveness contract: snapshot of the pre-fault status label so clearing
    // the fault restores the exact tier-band string the menu was showing. Non-null
    // only while the fault indicator is ON; independent of the power-save snapshot.
    private string? _preFaultStatusText;

    /// <summary>D3: tray-side Faulted indicator, mirroring
    /// <see cref="SetPowerSaveIndicator"/>. When set, the status header + OS
    /// tooltip become the reassuring "暫時無法偵測畫面 · 點一下重試 / Sensing
    /// paused — tap to retry" verdict (calm, never "ERROR"/red) and the prior
    /// label is stashed. When cleared, the prior label is restored; the next
    /// <see cref="ApplyState"/> tick (frames resume on recovery) repaints the live
    /// tooltip. Icon stays grey — never an alarm colour. Idempotent across
    /// repeated true / repeated false.</summary>
    public void SetFaultIndicator(bool faulted)
    {
        if (_statusItem is null) return;
        if (faulted)
        {
            _preFaultStatusText ??= _statusItem.Header as string;
            _statusItem.Header = "暫時無法偵測畫面 · 點一下重試 / Sensing paused — tap to retry";
            _tray.ToolTipText  = "SenseEase — 暫時無法偵測 · 點一下重試 / tap to retry";
        }
        else if (_preFaultStatusText != null)
        {
            _statusItem.Header = _preFaultStatusText;
            _preFaultStatusText = null;
        }
    }

    // ── M12 (TrayDistinct): code-drawn, colour-blind-safe tray icons ─────────
    //
    // The previous tray icon (TierColorPalette.IconForTier_Win) painted each
    // band as a flat rounded square that differed by HUE only. The grey
    // (102,102,102) → amber (230,140,40) step was ~35% perceptual distance and
    // collapsed to near-identical greys for a deuteranope / protanope, and
    // blurred together once the Win11 shell downscales 32 px → 16 px.
    //
    // Fix: render NATIVE frames at {16,24,32,48} px and pack them into one
    // multi-size ICO (BuildMultiSizeIco — uncompressed 32-bit BGRA DIB frames;
    // GDI+ has no ICO encoder so we hand-write the container). The old path
    // shipped ONE 32 px PNG frame and let the Win11 shell down-scale it to the
    // 16 px tray slot, which blurred/blanked the icon — the same PNG-at-16px bug
    // the app icon hit. Per-size frames let the shell pick the exact-size frame.
    // Each band still differs by SHAPE as well as hue so they are
    // distinguishable without relying on colour:
    //   • Grey  (calm)     — plain filled rounded square.
    //   • Amber (elevated) — saturated amber + a diagonal HATCH overlay. The
    //                        texture reads as "different" even in greyscale.
    //   • Red   (high)     — muted CLAY (never alarm red) + a filled-corner
    //                        wedge, a third distinct silhouette.
    // Calm-audience: no alarm red — the high band uses the same muted clay
    // (#B65C4E) the faulted bar uses. Reduced motion is N/A here: tray icons
    // are static (spec §1 "never animates"), so AccessibilityHelper.ReducedMotion
    // doesn't gate anything in this path.

    // Band hues. Amber bumped from the old (230,140,40) to a more saturated
    // #E67E1E so the colour step off grey is larger even before the hatch.
    private static readonly System.Drawing.Color TrayGreyFill  = System.Drawing.Color.FromArgb(255, 102, 102, 102); // calm grey, RGB 102
    private static readonly System.Drawing.Color TrayAmberFill = System.Drawing.Color.FromArgb(255, 0xE6, 0x7E, 0x1E); // #E67E1E saturated amber
    private static readonly System.Drawing.Color TrayClayFill  = System.Drawing.Color.FromArgb(255, 0xB6, 0x5C, 0x4E); // #B65C4E muted clay — NEVER alarm red

    // Shape-cue ink: a darker shade of each band's own hue so the hatch / wedge
    // reads as a textured carve-out rather than a second colour the user has to
    // decode. Kept fully opaque so it survives the 16 px downscale.
    private static readonly System.Drawing.Color TrayAmberHatchInk = System.Drawing.Color.FromArgb(255, 0x8A, 0x47, 0x0C); // deep amber for the hatch strokes
    private static readonly System.Drawing.Color TrayClayWedgeInk  = System.Drawing.Color.FromArgb(255, 0x7A, 0x35, 0x2A); // deep clay for the corner wedge

    /// <summary>M12: returns the colour-blind-safe, shape-cued tray icon for a
    /// band, cached so repeated same-band ticks reuse one <see
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

    /// <summary>Paints one tray frame: the rounded band-tinted "app tile", its
    /// colour-blind-safe shape cue (diagonal hatch for Medium, corner wedge for
    /// VeryLow), and the SenseEase brand lens — all scaled to <paramref
    /// name="body"/>. The lens DETAIL scales with size: at ≥24 px the full lens
    /// (translucent disc + ring + top-left highlight) is drawn; at 16–20 px the
    /// thin 1.6 px ring and the 3.4 px highlight both collapse to sub-pixel
    /// noise after the shell blends them, so only a simpler filled translucent
    /// disc is drawn. The band hue + shape cue are kept at every size so the
    /// Medium / VeryLow bands stay distinguishable even in the tiny tray slot.</summary>
    private static void PaintBand(
        System.Drawing.Graphics g,
        System.Drawing.Rectangle body,
        TierColorPalette.TierBand band)
    {
        // Legacy→5級 semantic mapping (preserve calm↔high intent):
        // Grey(calm)→VeryHigh, Amber(mid)→Medium, Clay(high)→VeryLow.
        System.Drawing.Color fill = band switch
        {
            TierColorPalette.TierBand.Medium  => TrayAmberFill,
            TierColorPalette.TierBand.VeryLow => TrayClayFill,
            _                                 => TrayGreyFill,
        };

        // Corner radius proportional to the body so the tile keeps the same
        // "app tile" silhouette at every size (≈19% of the body width — the
        // 6 px radius on the old 28 px body).
        int radius = System.Math.Max(2, body.Width * 6 / 28);

        using (var tile = RoundedRectPath(body, radius))
        using (var fillBrush = new System.Drawing.SolidBrush(fill))
        {
            g.FillPath(fillBrush, tile);
            g.SetClip(tile);
            switch (band)
            {
                case TierColorPalette.TierBand.Medium:
                    DrawDiagonalHatch(g, body, TrayAmberHatchInk);
                    break;
                case TierColorPalette.TierBand.VeryLow:
                    DrawCornerWedge(g, body, TrayClayWedgeInk);
                    break;
                // VeryHigh / other bands: plain fill — the textural
                // "calm / nothing layered on" cue.
            }
            g.ResetClip();
        }

        // Brand lens. At ≥24 px draw the full lens (ring + highlight); at the
        // tiny 16–20 px tray sizes those thin features vanish, so draw a single
        // filled translucent disc on the band-tinted tile instead.
        if (body.Width >= 24)
            DrawBrandLens(g, body);
        else
            DrawSimpleBrandDisc(g, body);
    }

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

    /// <summary>Paints the SenseEase brand lens — a translucent white disc with a
    /// soft ring and a top-left highlight — centred on the tray tile, echoing the
    /// AppIcon's lens over its tile grid. The band colour shows through the
    /// translucent disc, so each band keeps its hue while the mark stays
    /// recognizable (never a flat coloured square).</summary>
    private static void DrawBrandLens(System.Drawing.Graphics g, System.Drawing.Rectangle body)
    {
        // Lens diameter ≈57% of the body (16 px on the old 28 px body), scaled
        // so the mark keeps its proportions at 24 / 32 / 48 px frames.
        float dia = body.Width * 16f / 28f;
        float cx = body.X + body.Width / 2f;
        float cy = body.Y + body.Height / 2f;
        var lens = new System.Drawing.RectangleF(cx - dia / 2f, cy - dia / 2f, dia, dia);
        using (var disc = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(disc, lens);
        float ringWidth = System.Math.Max(1f, dia / 10f);
        using (var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 255, 255, 255), ringWidth))
            g.DrawEllipse(ring, lens);
        float hiDia = dia * 3.4f / 16f;
        float hiInset = dia * 2.6f / 16f;
        using (var hi = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 255, 255, 255)))
            g.FillEllipse(hi, cx - dia / 2f + hiInset, cy - dia / 2f + hiInset, hiDia, hiDia);
    }

    /// <summary>Simplified brand mark for the tiny 16–20 px tray frames: a
    /// single filled translucent disc (no thin ring, no 3.4 px highlight — both
    /// collapse to sub-pixel noise at 16 px after the shell blends them). The
    /// band hue still shows through the translucent disc, so the band stays
    /// readable and the mark keeps a recognizable round silhouette.</summary>
    private static void DrawSimpleBrandDisc(System.Drawing.Graphics g, System.Drawing.Rectangle body)
    {
        float dia = body.Width * 16f / 28f;
        float cx = body.X + body.Width / 2f;
        float cy = body.Y + body.Height / 2f;
        var lens = new System.Drawing.RectangleF(cx - dia / 2f, cy - dia / 2f, dia, dia);
        using var disc = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(210, 255, 255, 255));
        g.FillEllipse(disc, lens);
    }

    /// <summary>M12 amber shape cue: evenly-spaced diagonal stripes clipped to
    /// the icon body. A greyscale viewer still perceives the stripe texture, so
    /// amber is distinguishable from the plain grey band by shape, not hue.</summary>
    private static void DrawDiagonalHatch(System.Drawing.Graphics g, System.Drawing.Rectangle body, System.Drawing.Color ink)
    {
        // Stripe spacing / width scale with the body (7 px / 3 px on the old
        // 28 px body) so the hatch reads as the same texture at 16 / 32 / 48 px
        // rather than a few stray lines on the small frames.
        int spacing = System.Math.Max(3, body.Width * 7 / 28); // px between stripes
        float stroke = System.Math.Max(1.5f, body.Width * 3f / 28f); // stripe width
        using var pen = new System.Drawing.Pen(ink, stroke);
        var oldClip = g.Clip;
        try
        {
            // INTERSECT (not Replace) so the hatch stays inside the rounded-tile
            // clip PaintBand already set — otherwise the stripes would bleed into
            // the rounded corners (more visible at the small 16 px frame).
            g.SetClip(body, System.Drawing.Drawing2D.CombineMode.Intersect);
            // Sweep 45° lines from top-left to bottom-right across the body so
            // a few full-length diagonals land inside the clip region.
            for (int d = -body.Height; d < body.Width; d += spacing)
            {
                int x0 = body.Left + d;
                g.DrawLine(pen, x0, body.Top, x0 + body.Height, body.Bottom);
            }
        }
        finally
        {
            g.Clip = oldClip;
        }
    }

    /// <summary>M12 high-band shape cue: a filled triangular wedge in the
    /// bottom-right corner — a third distinct silhouette so the muted-clay high
    /// state can't be confused with the hatched amber or the plain grey, even
    /// before colour is considered.</summary>
    private static void DrawCornerWedge(System.Drawing.Graphics g, System.Drawing.Rectangle body, System.Drawing.Color ink)
    {
        int size = body.Width / 2; // wedge spans half the body
        var pts = new[]
        {
            new System.Drawing.Point(body.Right,        body.Bottom - size),
            new System.Drawing.Point(body.Right,        body.Bottom),
            new System.Drawing.Point(body.Right - size, body.Bottom),
        };
        using var wedgeBrush = new System.Drawing.SolidBrush(ink);
        g.FillPolygon(wedgeBrush, pts);
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
        // diag-4: stop the visibility watchdog so its DispatcherTimer +
        // closure don't pin the tray service after disposal.
        try { _visibilityWatchdog?.Stop(); } catch { }
        _vm.StateChanged -= OnStateChanged;
        // Phase 5 Step 3: static event — MUST unsubscribe or the tray
        // service (and its transitive graph: VM, TaskbarIcon, ContextMenu,
        // MenuItems, watchdog timer, cached HICONs) is pinned for the
        // process lifetime by the static invocation list.
        UserPreferences.Changed -= OnPreferencesChanged;
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
        // Fix 3: release any HICONs cached on TierColorPalette's own path
        // (IconForTier_Win) in case other callers populated it. The tray no
        // longer uses that path for its live icon (M12 draws locally), but the
        // cleanup stays so a stray cache entry can't leak until process exit.
        TierColorPalette.DisposeWinIconCache();
    }
}
