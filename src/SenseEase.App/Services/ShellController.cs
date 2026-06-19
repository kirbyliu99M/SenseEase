using System;
using System.Windows;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Models;

namespace SenseEase.App.Services;

/// <summary>
/// UI-RESHAPE — the coexistence shell that fronts the discreet new surfaces. MainWindow stays the
/// engine/tray/bar/card/overlay HOST (started hidden-to-tray); this controller, created from
/// <c>App.OnStartup</c>, owns the always-on edge sidebar and the on-demand popup bar/chat, bridging
/// them to the shared <see cref="MainViewModel"/> and a few small <c>internal</c> MainWindow wrappers.
/// Full host extraction is the deferred fuse plan; this is the lighter P0 that matches the locked
/// "keep MainWindow as host for now" decision.
/// </summary>
public sealed class ShellController : IShellController
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _console;
    private readonly SurfaceExcludeCoordinator _exclude;

    private readonly NpsService _nps = new();

    private EdgeSidebarWindow? _sidebar;
    private PopupBarWindow? _popup;
    private NpsSurveyWindow? _npsWindow;
    private ConsentBubbleWindow? _consentBubble;

    public ShellController(MainWindow console, MainViewModel vm)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _exclude = new SurfaceExcludeCoordinator(_vm.SetWgcMonitorExcludeRects);
    }

    /// <summary>Spawn the always-on edge sidebar + start engine-state fan-out. No console shown.</summary>
    public void Start()
    {
        _vm.StateChanged += OnVmStateChanged;

        // Item 1 — consent bubble: the VM raises these on the UI thread when the comfort overlay
        // wants to engage (or when a pending ask is no longer relevant). This controller owns the
        // bubble (it already owns the edge surfaces), so it belongs here, not in the console.
        _vm.OverlayConsentRequested += (_, _) => ShowConsentBubble();
        _vm.OverlayConsentDismissRequested += (_, _) => HideConsentBubble();

        _sidebar = new EdgeSidebarWindow();
        // Side-panel reveal (handle click / edge-throw): cursor is right here, so a SHORTER 5 s
        // cooldown before it vanishes is plenty (vs the tray's 10 s, where the cursor is far away).
        _sidebar.Clicked += (_, _) => RevealPopup(SidePanelGraceMs);
        _sidebar.RevealRequested += (_, _) => RevealPopup(SidePanelGraceMs);
        _sidebar.ContentRendered += (_, _) => _exclude.Register(_sidebar!);
        _sidebar.SizeChanged += (_, _) => _exclude.Update();
        _sidebar.LocationChanged += (_, _) => _exclude.Update();
        _sidebar.Show();

        // NPS KPI: count this session, then surface the subtle survey only if the gate allows
        // (≥3 sessions, 30-day cooldown, first-run complete, not recently dismissed). Dormant by
        // default — a fresh install never sees it.
        _nps.RecordSessionStart();
        MaybeShowNpsSurvey();
    }

    private void MaybeShowNpsSurvey()
    {
        if (_npsWindow is not null || !_nps.ShouldAsk()) return;
        var vm = new NpsSurveyViewModel(
            onSubmit: (score, comment) => { _nps.RecordResponse(score, comment); CloseNpsSurvey(); },
            onDismiss: () => { _nps.RecordDismissed(); CloseNpsSurvey(); });
        _npsWindow = new NpsSurveyWindow(vm);
        _npsWindow.ContentRendered += (_, _) => _exclude.Register(_npsWindow!);
        _npsWindow.LocationChanged += (_, _) => _exclude.Update();
        _npsWindow.SizeChanged += (_, _) => _exclude.Update();
        _npsWindow.Closed += (_, _) => { if (_npsWindow is { } w) _exclude.Unregister(w); _npsWindow = null; };
        _nps.RecordShown();
        _npsWindow.Show();
        _npsWindow.ReassertTopmost();
    }

    private void CloseNpsSurvey()
    {
        if (_npsWindow is null) return;
        var w = _npsWindow;
        _npsWindow = null;
        _exclude.Unregister(w);
        w.Close();
    }

    /// <summary>Reveal the glass popup bar (lazily created, condensed). Idempotent — a no-op if
    /// already visible. <paramref name="graceMs"/> = how long it stays open regardless of cursor
    /// distance before the proximity auto-close can vanish it (the reveal "cooldown").</summary>
    public void RevealPopup(int graceMs = 0)
    {
        EnsurePopup();
        if (_popup!.IsVisible) { if (graceMs > 0) _popup.GraceAutoClose(graceMs); return; }
        ShowPopup(graceMs);
    }

    /// <summary>Tray single-click reveal — the cursor is at the tray, far from the bar, so use the
    /// longer 10 s cooldown so the user has time to move to it.</summary>
    public void RevealFromTray() => RevealPopup(TrayGraceMs);

    /// <summary>Hotkey toggle: hide if shown, otherwise reveal with the tray-length cooldown (a
    /// hotkey summon, like the tray, leaves the cursor away from the bar).</summary>
    public void ToggleGlassPopupBar()
    {
        EnsurePopup();
        if (_popup!.IsVisible) _popup.DismissToHandle(); // condensed-baseline hide (no reopen flash) + VM reset
        else ShowPopup(TrayGraceMs);
    }

    private const int SidePanelGraceMs = 5_000;  // handle click / edge-throw — cursor is here
    private const int TrayGraceMs      = 10_000; // tray / hotkey — cursor is far, give time to reach

    private void EnsurePopup()
    {
        if (_popup is not null) return;
        _popup = new PopupBarWindow(this, _vm.ChatbotVm);
        _popup.ContentRendered += (_, _) => PositionPopup();
        _popup.SizeChanged += (_, _) => { PositionPopup(); _exclude.Update(); };
        _popup.LocationChanged += (_, _) => _exclude.Update();
        // Exclude registration + the edge-handle swap both follow VISIBILITY, so a self-hide
        // (proximity auto-close or click-away) clears the WGC rect AND brings the handle back —
        // not just an explicit toggle. While the bar is up the handle hides (the bar covers the
        // edge); when it collapses the discreet handle returns to anchor the next reveal.
        _popup.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { _exclude.Register(_popup!); _sidebar?.Hide(); }
            else
            {
                _exclude.Unregister(_popup!);
                // #4: re-anchor the edge handle to the popup's remembered centre so the handle and
                // the bar line up (the handle reveals where the bar was last left), then show it.
                _sidebar?.ReanchorToSavedCentre();
                _sidebar?.Show();
            }
        };
    }

    private void ShowPopup(int graceMs = 0)
    {
        if (_popup is null) return;
        _popup.Show();
        PositionPopup();
        _popup.ReassertTopmost();
        if (graceMs > 0) _popup.GraceAutoClose(graceMs);
        // NOTE: do NOT Activate() here — revealing the bar must not steal focus from the user's
        // app/game (low-disturbance). The popup activates ONLY when the chat opens (typing needs
        // the caret), handled in PopupBarWindow on IsChatOpen. ShowActivated=False keeps Show()
        // from grabbing focus.
    }

    // ── Item 1: consent bubble ────────────────────────────────────────────────
    private void ShowConsentBubble()
    {
        if (!_console.Dispatcher.CheckAccess()) { _console.Dispatcher.BeginInvoke(new Action(ShowConsentBubble)); return; }
        if (_consentBubble is null)
        {
            _consentBubble = new ConsentBubbleWindow();
            _consentBubble.ConsentAccepted += (_, _) => { _vm.GrantOverlayConsent(); _consentBubble?.Hide(); };
            _consentBubble.ConsentDeclined += (_, _) => { _vm.DeclineOverlayConsent(); _consentBubble?.Hide(); };
            _consentBubble.ContentRendered += (_, _) => PositionConsentBubble();
            _consentBubble.SizeChanged += (_, _) => { PositionConsentBubble(); _exclude.Update(); };
            _consentBubble.IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) _exclude.Register(_consentBubble!);
                else _exclude.Unregister(_consentBubble!);
            };
        }
        _consentBubble.Show();
        PositionConsentBubble();
        _consentBubble.ReassertTopmost();
    }

    private void HideConsentBubble()
    {
        if (!_console.Dispatcher.CheckAccess()) { _console.Dispatcher.BeginInvoke(new Action(HideConsentBubble)); return; }
        _consentBubble?.Hide();
    }

    /// <summary>Anchor the bubble at the right edge, vertically near the bar's remembered centre
    /// (the user associates the right edge with SenseEase), clamped to the work area.</summary>
    private void PositionConsentBubble()
    {
        if (_consentBubble is null) return;
        var work = SystemParameters.WorkArea;
        const double edgeGap = 12.0;
        _consentBubble.Left = Math.Max(work.Left, work.Right - _consentBubble.ActualWidth - edgeGap);
        double centreY = TryGetSavedBarCentreY() ?? (work.Top + work.Height / 2.0);
        double top = centreY - (_consentBubble.ActualHeight / 2.0);
        _consentBubble.Top = Math.Max(work.Top, Math.Min(work.Bottom - _consentBubble.ActualHeight, top));
    }

    private void PositionPopup()
    {
        if (_popup is null) return;
        var work = SystemParameters.WorkArea;
        // R3-7: the popup window is a FIXED 360x496 that never resizes (so a layered-window resize can't
        // flash). Its content (RootGrid) hugs the right edge + is vertically centred inside it, so docking
        // the WINDOW right with a small gap puts the bar a few px from the screen edge.
        const double edgeGap = 4.0;
        _popup.Left = Math.Max(work.Left, work.Right - _popup.ActualWidth - edgeGap);

        // #3/#5: anchor by the bar's CENTRE (drag up/down; default = screen centre). The bar is centred in
        // the fixed window, so the window centre = the bar centre. Clamp the BAR CENTRE on-screen (NOT the
        // whole window — it may overflow the work area transparently), so the bar can still sit near the
        // top/bottom edges instead of being confined to the middle by the tall fixed window.
        double centreY = TryGetSavedBarCentreY() ?? (work.Top + work.Height / 2.0);
        const double barHalf = 100.0; // keeps the ~172-tall condensed bar comfortably on-screen
        centreY = Math.Max(work.Top + barHalf, Math.Min(work.Bottom - barHalf, centreY));
        _popup.Top = centreY - (_popup.ActualHeight / 2.0); // window centred on the bar centre (may overflow)
    }

    private static double? TryGetSavedBarCentreY()
    {
        try { return Configuration.UserPreferences.Current.PopupBarCenterY; }
        catch { return null; }
    }

    private void OnVmStateChanged(object? sender, SenseState e)
    {
        // Engine ticks fire off the UI thread; marshal to the sidebar's dispatcher.
        if (_sidebar is { } bar && !bar.Dispatcher.CheckAccess())
        {
            bar.Dispatcher.BeginInvoke(new Action(() => OnVmStateChanged(sender, e)));
            return;
        }
        // The edge handle is now a neutral, discreet trapezoid (no tier tint) — only the
        // popup pulse reflects screen-motion, via StateChanged → PopupBarViewModel.ApplyState.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── IShellController ────────────────────────────────────────────────────
    // The popup meter shows the DISPLAYED level (engine tier floored by the always-on
    // baseline), so the +/- nudge is visibly reflected — not the raw engine tier.
    public int CurrentTier => _vm.CurrentState is { } s ? _vm.ResolveDisplayedTier(s.VimsTier) : _vm.VimsTier;
    public bool IsPaused => _console.IsPowerSaveActive;
    public event EventHandler? StateChanged;

    public void PlayPause()
    {
        _console.SetPowerSaveFromShell(!_console.IsPowerSaveActive);
        // Power-save toggling doesn't necessarily emit a VM StateChanged — nudge the
        // surfaces so the popup's play/pause affordance reflects the new state at once.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // The mask level +/- ride the 1–12 always-on baseline (MainViewModel.NudgeIntensityLevel),
    // which produces an IMMEDIATE VISIBLE change in both directions and is what the meter reads —
    // unlike EaseView/WidenView (FOV-only), which left the meter frozen (the "+/- not working" bug).
    public void NudgeStronger() { _vm.NudgeIntensityLevel(1); StateChanged?.Invoke(this, EventArgs.Empty); }
    public void NudgeGentler() { _vm.NudgeIntensityLevel(-1); StateChanged?.Invoke(this, EventArgs.Empty); }

    // Absolute level (the drag slider). Rides the same always-on baseline as the nudges, so the
    // displayed level + overlay update immediately and the meter reflects it.
    public int SetIntensityLevel(int level)
    {
        int displayed = _vm.SetIntensityLevel(level);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return displayed;
    }

    public void OpenChat()
    {
        if (_popup is null || !_popup.IsVisible) RevealPopup();
        if (_popup?.DataContext is PopupBarViewModel vm) vm.IsChatOpen = true;
    }

    public void OpenSettings() => _console.OpenSettingsFromShell();
    public void OpenLogs() => _console.OpenLogsFromShell();
    public void OpenConsole() => _console.SurfaceConsoleFromShell();

    public void RegisterSurface(IShellSurface surface) => _exclude.Register(surface);
    public void UnregisterSurface(IShellSurface surface) => _exclude.Unregister(surface);

    // ── UI-RESHAPE iter — quick-settings + LLM status (bridge to the VM) ──
    public bool AnchorGridEnabled
    {
        get => _vm.AnchorGridEnabled;
        set => _vm.AnchorGridEnabled = value;
    }
    public bool GradedExposureEnabled
    {
        get => _vm.GradedExposureEnabled;
        set => _vm.GradedExposureEnabled = value;
    }
    public bool PersistentMaskEnabled
    {
        get => _vm.PersistentOverlayEnabled;
        set => _vm.PersistentOverlayEnabled = value;
    }
    public bool IsLlmOnline => _vm.ChatbotVm?.IsLlmOnline ?? false;
}
