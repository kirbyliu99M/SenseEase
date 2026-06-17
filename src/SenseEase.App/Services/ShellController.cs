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
        _sidebar.Clicked += (_, _) => ToggleGlassPopupBar();
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

    /// <summary>Reveal / hide the glass popup bar (lazily created), positioned just left of the sidebar.</summary>
    public void ToggleGlassPopupBar()
    {
        if (_popup is null)
        {
            _popup = new PopupBarWindow(this, _vm.ChatbotVm);
            _popup.ContentRendered += (_, _) => PositionPopup();
            _popup.SizeChanged += (_, _) => { PositionPopup(); _exclude.Update(); };
            _popup.LocationChanged += (_, _) => _exclude.Update();
            // Exclude registration follows VISIBILITY, so a Deactivated-driven self-hide
            // (the click-away close) also clears the WGC rect — not just an explicit toggle.
            _popup.IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue) _exclude.Register(_popup!);
                else _exclude.Unregister(_popup!);
            };
            ShowPopup();
            return;
        }

        if (_popup.IsVisible) _popup.Hide();
        else ShowPopup();
    }

    private void ShowPopup()
    {
        if (_popup is null) return;
        _popup.Show();
        PositionPopup();
        _popup.ReassertTopmost();
        // Take focus so the chat input is typable; clicking any other window/desktop then
        // fires Deactivated → the popup hides itself (the "close on click-away" behaviour).
        _popup.Activate();
    }

    private void PositionPopup()
    {
        if (_popup is null) return;
        var work = SystemParameters.WorkArea;
        const double sidebarGap = 22.0; // sidebar width (16) + a small gap
        _popup.Left = Math.Max(work.Left, work.Right - _popup.ActualWidth - sidebarGap);
        _popup.Top = Math.Max(work.Top, work.Top + (work.Height - _popup.ActualHeight) / 2.0);
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
    public int CurrentTier => _vm.VimsTier;
    public bool IsPaused => _console.IsPowerSaveActive;
    public event EventHandler? StateChanged;

    public void PlayPause()
    {
        _console.SetPowerSaveFromShell(!_console.IsPowerSaveActive);
        // Power-save toggling doesn't necessarily emit a VM StateChanged — nudge the
        // surfaces so the popup's play/pause affordance reflects the new state at once.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NudgeStronger() => _vm.EaseView();
    public void NudgeGentler() => _vm.WidenView();

    public void OpenChat()
    {
        if (_popup is null || !_popup.IsVisible) ToggleGlassPopupBar();
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
