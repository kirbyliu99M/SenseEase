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

        _sidebar = new EdgeSidebarWindow();
        _sidebar.Clicked += (_, _) => RevealPopup();
        _sidebar.RevealRequested += (_, _) => RevealPopup();
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
    /// already visible. Fired by the edge handle's proximity throw + click. <paramref name="graceFromAfar"/>
    /// (tray / hotkey) keeps it open ~10 s so the user — whose cursor is far away — can reach it.</summary>
    public void RevealPopup(bool graceFromAfar = false)
    {
        EnsurePopup();
        if (_popup!.IsVisible) { if (graceFromAfar) _popup.GraceAutoClose(RevealGraceMs); return; }
        ShowPopup(graceFromAfar);
    }

    /// <summary>Hotkey toggle: hide the bar if shown, otherwise reveal it (with the from-afar grace —
    /// a hotkey summon, like the tray, leaves the cursor away from the bar).</summary>
    public void ToggleGlassPopupBar()
    {
        EnsurePopup();
        if (_popup!.IsVisible) _popup.Hide();
        else ShowPopup(graceFromAfar: true);
    }

    private const int RevealGraceMs = 10_000;

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

    private void ShowPopup(bool graceFromAfar = false)
    {
        if (_popup is null) return;
        _popup.Show();
        PositionPopup();
        _popup.ReassertTopmost();
        if (graceFromAfar) _popup.GraceAutoClose(RevealGraceMs);
        // NOTE: do NOT Activate() here — revealing the bar must not steal focus from the user's
        // app/game (low-disturbance). The popup activates ONLY when the chat opens (typing needs
        // the caret), handled in PopupBarWindow on IsChatOpen. ShowActivated=False keeps Show()
        // from grabbing focus.
    }

    private void PositionPopup()
    {
        if (_popup is null) return;
        var work = SystemParameters.WorkArea;
        // The bar docks near the right edge (the handle is hidden while it is up, so no gap is
        // needed for it); a few px of breathing room lets the Elev.3 glass shadow show.
        const double edgeGap = 4.0;
        _popup.Left = Math.Max(work.Left, work.Right - _popup.ActualWidth - edgeGap);

        // #3/#5: anchor by the bar's CENTRE (drag up/down; default = screen centre). Deriving Top
        // from the centre each time means a resize (expand / chat-open) extends symmetrically and
        // the always-on bar does NOT shift — it stays where the user left it.
        double centreY = TryGetSavedBarCentreY() ?? (work.Top + work.Height / 2.0);
        double top = centreY - (_popup.ActualHeight / 2.0);
        _popup.Top = Math.Max(work.Top, Math.Min(work.Bottom - _popup.ActualHeight, top));
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
