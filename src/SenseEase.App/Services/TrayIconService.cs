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
        string header = $"VIMS 第 {state.VimsTier}/12 · 動態感 {feel}";
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
        _tray.ToolTipText = $"SenseEase — 第 {state.VimsTier}/12 · 動態感 {feel}";

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
    // Fix: keep the 32 px canvas and the proven PNG-in-ICO wrapper (the only
    // path that registers reliably with the Hardcodet tray on .NET 8 — GDI+
    // has no ICO encoder), but make the three bands differ by SHAPE as well as
    // hue so they are distinguishable without relying on colour:
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
    /// cref="Dispose"/>. The 32 px canvas + PNG-in-ICO wrapper match the
    /// previously-shipping path; only the painting (saturated amber + hatch,
    /// clay + corner wedge) differs.</summary>
    private System.Drawing.Icon BuildDistinctTrayIcon(TierColorPalette.TierBand band)
    {
        if (_distinctIconCache.TryGetValue(band, out var cached)) return cached;

        // 32×32 GDI+ bitmap with a 2 px transparent margin (matches the prior
        // icon's bounding box so the shell layout is unchanged).
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Body rectangle (inside the 2 px margin), same 28×28 footprint the
            // old flat icon used.
            var body = new System.Drawing.Rectangle(2, 2, 28, 28);

            // Legacy→5級 semantic mapping (preserve calm↔high intent):
            // Grey(calm)→VeryHigh, Amber(mid)→Medium, Red(high)→VeryLow.
            System.Drawing.Color fill = band switch
            {
                TierColorPalette.TierBand.Medium  => TrayAmberFill,
                TierColorPalette.TierBand.VeryLow => TrayClayFill,
                _                                 => TrayGreyFill,
            };

            using (var fillBrush = new System.Drawing.SolidBrush(fill))
                g.FillRectangle(fillBrush, body);

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
        }

        byte[] icoBytes = BuildPngWrappedIco(bmp);
        // Icon(Stream) reads the stream eagerly in the ctor, so disposing the
        // MemoryStream right after is safe — matches the prior TierColorPalette
        // path. The Icon owns the HICON it allocates; we Dispose() it in Dispose.
        using var stream = new MemoryStream(icoBytes);
        var icon = new System.Drawing.Icon(stream);
        _distinctIconCache[band] = icon;
        return icon;
    }

    /// <summary>M12 amber shape cue: evenly-spaced diagonal stripes clipped to
    /// the icon body. A greyscale viewer still perceives the stripe texture, so
    /// amber is distinguishable from the plain grey band by shape, not hue.</summary>
    private static void DrawDiagonalHatch(System.Drawing.Graphics g, System.Drawing.Rectangle body, System.Drawing.Color ink)
    {
        const int spacing = 7;   // px between stripes — coarse enough to survive 16 px downscale
        const float stroke = 3f; // stripe width
        using var pen = new System.Drawing.Pen(ink, stroke);
        var oldClip = g.Clip;
        try
        {
            g.SetClip(body);
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

    /// <summary>M12: builds an in-memory ICO wrapping the bitmap as a PNG
    /// (Win Vista+ accepts PNG-in-ICO). Mirrors the proven wrapper that ships
    /// the tray icon today — GDI+ on .NET 8 has no ICO encoder, so we write the
    /// 6-byte ICONDIR + 16-byte ICONDIRENTRY ourselves and lean on the working
    /// PNG encoder for the payload. Width/Height are single BYTES; BitCount is
    /// 32 (not 0) for the Win11 shell renderer.</summary>
    private static byte[] BuildPngWrappedIco(System.Drawing.Bitmap bmp)
    {
        byte[] pngBytes;
        using (var pngStream = new MemoryStream())
        {
            bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            pngBytes = pngStream.ToArray();
        }

        using var icoStream = new MemoryStream();
        using (var writer = new BinaryWriter(icoStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ── ICONDIR (6 bytes) ──
            writer.Write((ushort)0);   // Reserved
            writer.Write((ushort)1);   // Type = 1 (ICO)
            writer.Write((ushort)1);   // ImageCount = 1
            // ── ICONDIRENTRY (16 bytes) ──
            writer.Write((byte)32);    // Width  (BYTE; 0 = 256)
            writer.Write((byte)32);    // Height (BYTE; 0 = 256)
            writer.Write((byte)0);     // ColorCount: 0 for PNG / ≥256 colours
            writer.Write((byte)0);     // Reserved
            writer.Write((ushort)1);   // Planes
            writer.Write((ushort)32);  // BitCount: 32 (RGBA), not 0
            writer.Write((uint)pngBytes.Length); // ImageSize
            writer.Write((uint)22);    // ImageOffset = 6 + 16
        }
        icoStream.Write(pngBytes, 0, pngBytes.Length);
        return icoStream.ToArray();
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
