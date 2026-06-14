using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SenseEase.App.Configuration;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;

namespace SenseEase.App;

/// <summary>Dashboard window. Hosts the view model and manages the overlay window.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private OverlayWindow? _overlay;
    private SpikePopupWindow? _spikePopup;

    // Path Z UX iter 5: tray-resident lifecycle. _quittingFromTray flips to
    // true when the tray's "退出 SenseEase" menu fires Application.Shutdown
    // so the OnClosing intercept knows to let the close through instead of
    // hiding the window. Without this, Quit-from-tray would just minimize
    // MainWindow back to the tray (infinite loop, never quits).
    private bool _quittingFromTray;

    // v0.8 hotfix follow-up: Power Save also dismisses the comfort overlay.
    // _overlayVisibleBeforePowerSave snapshots the user's overlay state at
    // the moment Power Save is engaged so it can be restored on disable.
    // Default true — startup default is "overlay engaged" so the snapshot
    // before any user interaction is consistent with the on-by-default
    // mental model.
    private bool _overlayVisibleBeforePowerSave = true;

    // UI Consolidation Sprint Phase 3 fix-round-2 (2026-05-30, verification
    // HIGH #D): monotonic epoch for coalescing rapid Power Save toggles.
    // The SemaphoreSlim _lifecycleGate inside SidecarHeadPoseReader
    // serializes EXECUTION but ThreadPool worker ordering is not FIFO, so
    // rapid Task.Run dispatch of Stop/Restart can be picked up out of order
    // — final state may not match the user's last click. Epoch comparison
    // inside each Task.Run body short-circuits any task whose dispatch is
    // no longer the latest, so only the most recent ApplyPowerSave's
    // operation actually runs. Combined with the in-sidecar gate, this
    // gives both serialization AND order preservation.
    //
    // Phase 4 clarification (UVHS Playbook clause 5 follow-up): coalescing
    // is BEST-EFFORT, not guaranteed. The window between Increment-of-epoch
    // and the in-Task.Run Read is ~microseconds, but a task that loads its
    // myEpoch BEFORE the next click's Increment will pass the short-circuit
    // check and still execute Stop/Restart even though a newer click is
    // already in flight. The in-sidecar _lifecycleGate then serialises the
    // newer click's execution behind it, so final state still matches the
    // user's last click — the coalescing just stops being free (an extra
    // Stop/Restart round runs). This is acceptable: the lifecycle gate is
    // the load-bearing correctness guarantee, the epoch is a perf hint.
    private long _powerSaveDispatchEpoch;
    // Pop-up sprint Task 1: BarStub for WGC mask validation. Spawned only when
    // env var SENSEEASE_WGC_BAR_VALIDATION=1 is set at app launch. See OnLoaded.
    private BarStubWindow? _wgcBarStub;
    // Pop-up sprint Task 7 patch: in-app banner that surfaces validation-mode
    // diagnostic text so a user whose env var leaked across PowerShell
    // sessions sees the warning instead of filing a phantom bug.
    private ValidationModeBanner? _validationBanner;
    // Pop-up sprint Task 2: the persistent ambient bar. Spawned on Loaded
    // (production path), driven by VimsTier changes via StateChanged.
    private PersistentBarWindow? _persistentBar;
    // Pop-up sprint Task 3: system tray icon. Lifetime tied to MainWindow;
    // disposed on Closed so the shell unregisters cleanly. Lives in parallel
    // with the bar — colour-synced via the shared TierColorPalette.
    private TrayIconService? _trayIcon;
    // Pop-up sprint Task 4: expanded notification card + mute controller.
    // Card window is hidden by default and Show()-ed on bar/tray click. The
    // controller is in-memory mute state (persistence lands in Task 5).
    private NotificationCardWindow? _cardWindow;
    private readonly NotificationCardController _cardController = new();
    // Tracks the previous tier band so tier-up auto-expand only fires on
    // the band-crossing edge, not on every state push within the same band.
    // Initialised to VeryHigh (the default startup state — 極高 / calmest band,
    // legacy Grey under the 5級 動態感 relabel).
    private TierColorPalette.TierBand _lastBand = TierColorPalette.TierBand.VeryHigh;

    // Blank-dashboard-on-autostart fix (2026-06-09): true while the --minimized autostart
    // window is parked off-screen awaiting its first render (see ctor + ContentRendered).
    // A WPF window Hide()-d before its first render composes no surface → it paints
    // permanently BLANK when later surfaced from the tray/card; parking it off-screen lets
    // it render safely instead, and we Hide() only after the render is confirmed.
    private bool _trayStartupPark;
    private const int OffScreenParkPx = -32000;   // standard off-screen park; still composites/renders

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // M2 (Phase 6c): wire the Power Save lookup so the chatbot's
        // [habituation_state] block reflects the current sensing state.
        // MainWindow owns the source of truth (session-scoped per
        // Pop-up sprint iter 2); the VM reads through this callback.
        _viewModel.SetPowerSaveActiveLookup(() => _powerSaveActive);

        // UI Consolidation Sprint M-Phase-1 (2026-05-30): wire the new
        // "設定" tab's shared SettingsContent. Same vestibular lookup +
        // PreferencesChanged-handler pattern that ShowSettingsWindow uses
        // for the tray-menu / Pop-up Card SettingsWindow instance, so a
        // toggle flipped in the tab repaints bar/tray identically.
        SettingsTabContent.HasVestibularDiagnosis = _viewModel.HasVestibularDiagnosis;
        // Alpha-2 addendum (auto-profiling world): wire the vestibular "regret" toggle +
        // the moved-into-Settings Reset to the VM. Hidden unless AutoProfilingEnabled.
        SettingsTabContent.GetVestibularProtection = () => _viewModel.VestibularProtectionEnabled;
        SettingsTabContent.SetVestibularProtection = v => _viewModel.VestibularProtectionEnabled = v;
        SettingsTabContent.ResetRequested          = () => _viewModel.ResetStateCommand.Execute(null);
        // D-1 (CW-1 step 3): wire the 漸進適應 / Graded exposure toggle to the VM so the
        // engine pause flips IN-SESSION (not only at next-launch hydration). Read = live
        // engine state; write = MainViewModel.GradedExposureEnabled (engine flip + persist).
        SettingsTabContent.GetGradedExposure       = () => _viewModel.GradedExposureEnabled;
        SettingsTabContent.SetGradedExposure       = v => _viewModel.GradedExposureEnabled = v;
        // Event Log relocated into Settings (2026-06-07): the 設定 tab's button opens
        // the dedicated EventLogWindow. Wired only where a MainViewModel exists (here +
        // the tray SettingsWindow below); the card hides itself when this is unwired.
        SettingsTabContent.OpenEventLog            = ShowEventLogWindow;
        // FEAT-08 (CW-10, B1b UI half): wire the live head-tracking health read so the
        // 設定 tab's status caption reflects the real sidecar state (運作中 / 無畫面 / 已停止)
        // instead of staying permanently hidden. Reads the SAME real reader the Power
        // Save lifecycle drives (MainViewModel.HeadPoseReader, not the dev mock-sweep
        // override); when head-tracking is off the reader is the no-op DefaultHeadPoseReader,
        // so we report Dead ("已停止") — the sidecar is not running. Both hosts wire this,
        // mirroring the GradedExposure / Vestibular host-delegate pattern.
        SettingsTabContent.GetHeadTrackingHealth   = GetHeadTrackingHealth;
        SettingsTabContent.PreferencesChanged    += (_, _) => RefreshSettingsDependentSurfaces();

        _viewModel.OverlayVisibilityChanged += OnOverlayVisibilityChanged;
        _viewModel.StateChanged             += OnStateChanged;
        _viewModel.LivenessChanged          += OnLivenessChanged; // D3: fan Faulted out to bar + tray

        if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
        {
            vm.ChatbotVm.Messages.CollectionChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()));
            ChatScroll.Loaded        += (_, _) => ChatScroll.ScrollToEnd();
            ChatScroll.IsVisibleChanged += (_, _) =>
            {
                if (ChatScroll.IsVisible)
                    Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()));
            };

            // Pop-up sprint Task 6: dispatcher between new (bar/tray/card)
            // and legacy (tab-switch + chat bubble + SpikePopupWindow)
            // notification paths. Default UseNewBarOverlay=true → new path.
            // The flag isn't exposed in the settings UI (emergency-rollback
            // only); edit user-preferences.json directly to roll back.
            vm.PressureSpikeDetected += (_, magnitude) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (UserPreferences.Current.UseNewBarOverlay)
                    {
                        // ── New path ─────────────────────────────────────
                        // Bar colour already updated by StateChanged. Card
                        // auto-expand only if Toggle A is ON (default OFF —
                        // passive signal first). 6-hour mute honoured via
                        // ShowNotificationCardForCurrentTier(userInitiated:false).
                        if (UserPreferences.Current.AutoExpandNotifications)
                        {
                            ShowNotificationCardForCurrentTier(userInitiated: false);
                        }
                    }
                    else
                    {
                        // ── Legacy path ──────────────────────────────────
                        // Tab switch + chat bubble + floating SpikePopupWindow.
                        // Cooldown is enforced upstream by PressureSpikeDetector.
                        bool zhHint = System.Globalization.CultureInfo.CurrentUICulture
                                        .TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
                        var (q, opts) = SenseEase.Infrastructure.Nlp.FeedbackLexicon
                                            .BuildSpikeFollowup(zhHint, magnitude);

                        // Switch to the AI Assistant tab (where the chatbot followup
                        // lands). Target it BY REFERENCE — the prior SelectedIndex=2 went
                        // stale when the Event Log got its own tab (it pointed at the log,
                        // not the chat); removing the Event Log tab (2026-06-07) makes the
                        // by-name select robust to any further tab reordering.
                        if (MainTabs is not null && AiAssistantTab is not null)
                            MainTabs.SelectedItem = AiAssistantTab;
                        vm.ChatbotVm.RaiseFollowup(q, opts);

                        if (_spikePopup is not null && _spikePopup.IsLoaded)
                        {
                            try { _spikePopup.Close(); } catch { }
                        }
                        var popup = new SpikePopupWindow(q, opts);
                        popup.OptionSelected += answer => vm.ChatbotVm.SubmitFollowupAnswer(answer);
                        popup.Closed += (_, _) => { if (ReferenceEquals(_spikePopup, popup)) _spikePopup = null; };
                        _spikePopup = popup;
                        popup.Show();
                    }
                }));
            };
        }

        // M3 7e production wiring: when MainViewModel validates a chatbot
        // proposal, surface it in the Card. Card events route back through
        // the VM's Accept/Decline/Defer methods.
        _viewModel.ProposalSurfaceRequested += OnProposalSurfaceRequested;

        // ── Eye-Protection chart bootstrap ─────────────────────────────────────
        Loaded += (_, _) =>
        {
            EnsureChartInitialized();
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm2)
            {
                // Coalescing: mark dirty on each ring-buffer append; a 15 Hz timer
                // redraws once per interval only when data has actually changed.
                vm2.OfiHistory.CollectionChanged      += (_, _) => _chartDirty = true;
                vm2.PressureHistory.CollectionChanged += (_, _) => _chartDirty = true;
                _chartTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(67) };
                _chartTimer.Tick += (_, _) =>
                {
                    // Only redraw when the chart is actually on-screen. It is now
                    // collapsed on the Eye-Care tab (advanced telemetry lives on the
                    // Dashboard), so this stays dormant; it also skips redraws while
                    // the Eye-Care tab is not the active one.
                    if (_chartDirty && PressureChartCanvas.IsVisible) { _chartDirty = false; RedrawChart(); }
                };
                _chartTimer.Start();
            }
        };

        // Path Z UX iter 5 + blank-dashboard fix (2026-06-09): at Windows-login autostart
        // the app launches with --minimized and must come up hidden-to-tray. The ORIGINAL
        // code Hide()-d the window at SourceInitialized — but a WPF window hidden BEFORE its
        // first render composes no surface, so the later tray/card "Open Dashboard" Show()
        // paints a permanently-BLANK dashboard (exact symptom: blank, login-autostart only,
        // never recovers). Fix: PARK the window off-screen + non-activating so it renders
        // without being seen (no flash, no taskbar button, no focus-steal at login) instead
        // of hiding it; then Hide() it only once it has actually rendered (ContentRendered),
        // restoring an on-screen position so the eventual Show() surfaces a real dashboard.
        if (App.StartMinimizedToTray)
        {
            _trayStartupPark      = true;
            ShowActivated         = false;                          // pre-show: no focus-steal at login
            ShowInTaskbar         = false;                          // no taskbar button during the off-screen render
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = OffScreenParkPx;                                 // off-screen, but still composites/renders
            Top  = OffScreenParkPx;
        }

        SourceInitialized += (_, _) =>
        {
            // Taskbar-icon fix (dotnet/wpf#11308): on Windows 11 the taskbar object
            // initialises BEFORE a window created after a heavy startup (SenseEase
            // loads ML / WGC), so the taskbar captures a DEFAULT icon while Window.Icon
            // still paints the correct title-bar / Alt-Tab icon — exactly the blank-
            // taskbar symptom. Push our own embedded icon onto the window's Win32 class
            // icons (SetClassLongPtr) + WM_SETICON here so the taskbar uses it instead
            // of the default. Best-effort; the HWND exists by SourceInitialized.
            SenseEase.App.Interop.ShellTaskbarIcon.RegisterForWindow(this);
            // NOTE: the autostart Hide() that used to live here is GONE — hiding before the
            // first render is exactly what caused the blank-dashboard regression. The window
            // is parked off-screen (above) and Hide()-d in ContentRendered once it's rendered.
        };

        // Belt-and-suspenders: re-apply once content has rendered and the taskbar
        // button definitely exists, covering the race where the button is created a
        // hair after SourceInitialized. Idempotent — re-sets the same embedded icon.
        // Also resolves the autostart off-screen park: the window has now composed a real
        // surface, so it is finally safe to Hide() it + bring its on-screen placement back.
        ContentRendered += (_, _) =>
        {
            SenseEase.App.Interop.ShellTaskbarIcon.RegisterForWindow(this);
            if (_trayStartupPark)
            {
                _trayStartupPark = false;
                UnparkOffscreenWindow(hideAfter: true);
            }
        };

        // Display Comfort decommissioned at v0.11 (UI Consolidation Sprint
        // Phase 4). The historical TODO(lux-removal) revive seam is removed
        // — the entire DDC/CI pipeline (WireLuxComfort, LuxComfortController,
        // WebcamLuxProvider, LightSensorProvider, MockAmbientLightProvider,
        // LuxComfortMapper, IAmbientLightProvider) is deleted. See
        // CHANGELOG.md v0.11 "Removed".
        Closed += (_, _) =>
        {
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
            {
                vm.DisposeServices();
            }
        };
        Closed += (_, _) => _overlay?.Close();
        Closed += (_, _) => _spikePopup?.Close();

        // Path Z UX iter 2: bar-click bug fix. After Alt-Tab to another
        // window, multi-monitor drag, or MainWindow minimize/restore, the
        // bar's z-order can get demoted behind whatever app stole focus
        // (some apps Topmost themselves, some fullscreen-exclusive). Re-
        // assert the bar's Topmost on every MainWindow activation +
        // restore-from-minimized event. Cheap (one SetWindowPos round-trip).
        // ABCD test fix (2026-06-12): the OVERLAY gets the same recovery — one of the
        // four test machines lost it behind a topmost-claiming app permanently.
        Activated += (_, _) =>
        {
            _persistentBar?.ReassertTopmost();
            if (_overlay is { IsVisible: true }) _overlay.ReassertTopmost();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal || WindowState == WindowState.Maximized)
            {
                _persistentBar?.ReassertTopmost();
                if (_overlay is { IsVisible: true }) _overlay.ReassertTopmost();
            }
        };

        // v0.8 hotfix: click on MainWindow (the dashboard) also dismisses
        // the card. The foreground-process check inside NotificationCardWindow
        // can't catch this because MainWindow shares our process — same PID
        // means the card considers it "ours" and keeps itself open. Adding
        // PreviewMouseDown here gives the explicit "click anywhere on the
        // dashboard closes the card" semantic the user asked for.
        // PreviewMouseDown bubbles through all child controls so a click on
        // any element of the dashboard (chart, button, tab, list) triggers.
        PreviewMouseDown += (_, _) => _cardWindow?.Hide();
        // Stop the 15 Hz chart timer so the DispatcherTimer + VM ref don't pin the
        // window after Close (kept the Tick closure alive otherwise).
        Closed += (_, _) => _chartTimer?.Stop();
        Closed += (_, _) => _wgcBarStub?.Close();
        Closed += (_, _) => _validationBanner?.Close();
        Closed += (_, _) => _persistentBar?.Close();
        Closed += (_, _) => _trayIcon?.Dispose();
        Closed += (_, _) => _cardWindow?.Close();

        // ── Pop-up sprint Task 1: WGC bar-validation harness ────────────────
        // When SENSEEASE_WGC_BAR_VALIDATION=1, spawn a BarStubWindow at the bar's
        // intended position (top-centre, 4×80) with a 200 ms red↔cyan adversarial
        // animation. The WGC trace log writes one OFI sample per tick to
        // %TEMP%/wgc_ofi_trace.txt (see WgcSensingProvider.OnTick).
        //
        // When SENSEEASE_WGC_BAR_MASK=1 is ALSO set, the BarStub's monitor-coords
        // rectangle is fed to WgcSensingProvider.SetMonitorExcludeRect so the
        // mask zeroes those cells in the downsampled frame before MAD. This is
        // the test for Option B (pixel-mask the bar rect in the OFI pipeline);
        // expected outcome: trace shows OFI flat while the bar animates.
        //
        // Both env vars unset = zero behavioural difference vs production.
        //
        // overlay-bar-redesign diag-2: each Loaded handler wrapped in
        // entry/exit/exception logs so a silent break in the chain is
        // visible in wgc_diag.txt without re-instrumenting. The throw
        // after the catch preserves the original WPF DispatcherUnhandledException
        // semantics — production crash behaviour is unchanged.
        Loaded += (_, _) =>
        {
            SafeLog("[Loaded#1] WGC validation entry");
            try
            {
                MaybeStartWgcValidation();
                SafeLog("[Loaded#1] WGC validation exit OK");
            }
            catch (Exception ex)
            {
                SafeLog($"[Loaded#1] WGC validation EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        };
        // Pop-up sprint Task 2: spawn the persistent ambient bar AFTER the WGC
        // validation block so a validation run isn't accidentally fighting the
        // production bar over the same monitor rect. In a validation run
        // (SENSEEASE_WGC_BAR_VALIDATION=1) the BarStub owns the mask + screen
        // position; in a production run the BarStub never spawns and the
        // persistent bar owns both.
        Loaded += (_, _) =>
        {
            SafeLog("[Loaded#2] StartPersistentBar entry");
            try
            {
                StartPersistentBar();
                SafeLog("[Loaded#2] StartPersistentBar exit OK");
            }
            catch (Exception ex)
            {
                SafeLog($"[Loaded#2] StartPersistentBar EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        };
        // Pop-up sprint Task 3: spawn tray icon AFTER the bar so the tray's
        // initial "toggle bar visibility" label correctly reflects the bar's
        // initial visible state. Same validation-env-var mutex as the bar.
        Loaded += (_, _) =>
        {
            SafeLog("[Loaded#3] StartTrayIcon entry");
            try
            {
                StartTrayIcon();
                SafeLog("[Loaded#3] StartTrayIcon exit OK");
            }
            catch (Exception ex)
            {
                SafeLog($"[Loaded#3] StartTrayIcon EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        };

        // v0.8 hotfix follow-up: comfort overlay engaged by default at
        // startup. User mental model: opening the app = protection on.
        // The OverlayWindow lives in fully-transparent state at low tiers
        // (alpha 0 in OverlayWindow's VimsLevels table for tier 1), so
        // visually nothing changes until pressure rises. User can disable
        // via the existing ToggleOverlayCommand if they want the screen
        // unfiltered. Power Save snapshots+restores this flag.
        // Subscribed via Loaded so OverlayWindow construction happens
        // AFTER the visual tree is ready.
        Loaded += (_, _) =>
        {
            if (!_viewModel.OverlayVisible) _viewModel.OverlayVisible = true;
        };

        // DEAD-14 (D-10, 2026-06-11): developer/QA hotkeys are now gated behind
        // the SENSEEASE_DEV_HOTKEYS env var (same opt-in pattern as
        // SENSEEASE_WGC_BAR_VALIDATION). Unset (the alpha-tester default) ⇒ the
        // whole PreviewKeyDown handler is a no-op, so a tester can no longer trip
        // a fake/simulated fault by fat-fingering Ctrl+Shift+F. Set =1 at launch
        // to arm the two dev hotkeys below. See docs/DEBUGGING.md for the full
        // dev-hotkey list + the env var.
        //
        // The M5.1-B double-tap-Ctrl escape hatch that used to live here is GONE
        // — the feature was removed entirely per D-10 (OverlayWindow.TriggerEscapeHatch
        // deleted, AppConfig.HeadTrackingEscapeHatchEnabled retired). The mask is
        // never weakened; nothing here lowers FOV opacity any more.
        if (DevHotkeysEnabled)
        {
            // Ctrl+Shift+H: developer mock head-pose sweep. 20-second deterministic
            // pattern (horizontal → vertical → circular → center hold). Independent
            // of AppConfig.HeadTrackingEnabled — used during QA to prove the wiring
            // end-to-end on machines without a working webcam. Second press while a
            // sweep is in flight is a no-op (StartMockHeadPoseSweep returns early if
            // _overrideReader is already non-null).
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.H &&
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift)
                {
                    _viewModel.StartMockHeadPoseSweep();
                    SafeLog("[DevHotkey] Ctrl+Shift+H → mock head-pose sweep started");
                    e.Handled = true;
                    return;
                }

                // Ctrl+Shift+F: dev/QA visual-verification of the Faulted bar/tray
                // treatment. Toggles a SIMULATED liveness fault so the Faulted
                // treatment can be eyeballed without crashing the GPU. The
                // underlying pipeline is untouched (the provider only raises health
                // on its OWN transitions, so a healthy live provider won't override
                // this forced state until it's toggled back). Because this is a
                // SIMULATED fault, the event-log entry below is explicitly labeled
                // "simulated" so it can never be mistaken for a real sensing fault
                // in a field report.
                if (e.Key == Key.F &&
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift)
                {
                    _devFaultForced = !_devFaultForced;
                    _viewModel.TestHook_RaiseSensingHealth(!_devFaultForced);
                    SafeLog($"[DevHotkey] Ctrl+Shift+F → SIMULATED liveness fault = {_devFaultForced}");
                    LogSimulatedFaultEvent(_devFaultForced);
                    e.Handled = true;
                    return;
                }
            };
        }

        // M4 head-tracking sprint: spawn the Python sidecar if
        // HeadTrackingEnabled is true. Idempotent + flag-off invariant
        // safe: returns immediately when the flag is false (no process
        // spawned, byte-equivalent to M3 baseline). Wrapped in the same
        // entry/exit log pattern as the other Loaded handlers so a silent
        // sidecar-start failure is visible in wgc_diag.txt.
        Loaded += (_, _) =>
        {
            SafeLog("[Loaded#4] StartHeadTrackingIfEnabled entry");
            try
            {
                _viewModel.StartHeadTrackingIfEnabled();
                SafeLog("[Loaded#4] StartHeadTrackingIfEnabled exit OK");
            }
            catch (Exception ex)
            {
                SafeLog($"[Loaded#4] StartHeadTrackingIfEnabled EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                // Don't rethrow — head-tracking is opt-in and must never
                // block the dashboard from coming up.
            }
        };

        // M5 UX-iter 3 Fix C (2026-05-29) + M5.1-C iter 2 (2026-05-30):
        // brush position update was originally a 30 Hz DispatcherTimer (Fix
        // C) but PRECONDITION measurement showed it only achieved ~20 Hz
        // under load due to dispatcher contention. On the user's 165 Hz
        // monitor, that meant brush.Center was held unchanged for 8 render
        // frames between updates — a visibly choppy 20 fps animation on a
        // 165 fps display.
        //
        // M5.1-C iter 2: subscribe to CompositionTarget.Rendering instead
        // of a DispatcherTimer. Rendering fires once per WPF composition
        // pass (i.e. at display refresh rate, 165 Hz on this monitor) and
        // is the standard hook for frame-rate-decoupled animation. On each
        // render frame we pull the latest sample from the reader and
        // interpolate the displayed brush position toward it with a short
        // half-life (~15 ms). brush.Center mutates 165 times per second
        // with sub-frame motion between sample arrivals — perceived smooth
        // motion. Sidecar still produces at 15 Hz; the interpolation
        // tweens between those discrete sample arrivals.
        //
        // Cost: ~10 µs × 165 Hz = 1.65 ms/sec ≈ 0.17% of one core.
        Loaded += (_, _) =>
        {
            // NE25: only run the per-frame head-pose pump when head-tracking is
            // enabled. Default is OFF, in which case ReadLatest() is always null
            // and the per-frame lerp is a pure no-op — yet it would still fire at
            // monitor refresh (~165 Hz) for the whole app lifetime. The mask is
            // still held centered on every engine tick via the state-driven
            // ApplyFovMask path (OverlayWindow active=false → center 0.5,0.5), so
            // skipping the pump changes nothing visible when head-tracking is off.
            if (AppConfig.HeadTrackingEnabled)
            {
                SafeLog("[Loaded#5] HeadPoseRendering subscribe (head-tracking on)");
                SetHeadPoseRenderingSubscribed(true);
            }
            else
            {
                SafeLog("[Loaded#5] HeadPoseRendering skipped — head-tracking off (NE25)");
            }
        };
        Unloaded += (_, _) => SetHeadPoseRenderingSubscribed(false);
    }

    /// <summary>Tab transition: cross-fade the selected tab content on switch so
    /// changing tabs reads as a smooth fade rather than a hard cut. Respects Reduce
    /// Motion (skips the fade) and ignores SelectionChanged bubbled up from inner
    /// selectors (a ListBox / ComboBox / nested TabControl inside a tab) — only
    /// MainTabs' OWN selection animates. PART_SelectedContentHost is the default
    /// TabControl template's content presenter; the cast is null on the very first
    /// pass before the template is applied, which simply skips the fade that once.</summary>
    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TabControl tc || !ReferenceEquals(tc, MainTabs)) return;
        if (SenseEase.App.Accessibility.AccessibilityHelper.ReducedMotion()) return;

        if (MainTabs.Template?.FindName("PART_SelectedContentHost", MainTabs)
                is System.Windows.Controls.ContentPresenter host)
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                new Duration(TimeSpan.FromMilliseconds(190)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            host.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);
        }
    }

    /// <summary>NE25: idempotently subscribe/unsubscribe the per-frame
    /// CompositionTarget.Rendering head-pose pump. Subscribed only while
    /// head-tracking is enabled AND the sidecar is live; parked during Power
    /// Save in lock-step with the sidecar Stop/Restart in ApplyPowerSave, so a
    /// stopped sidecar produces no idle per-frame work. Must run on the UI
    /// thread (CompositionTarget.Rendering is dispatcher-affined).</summary>
    private void SetHeadPoseRenderingSubscribed(bool on)
    {
        if (on == _renderingSubscribed) return;
        if (on)
        {
            try
            {
                System.Windows.Media.CompositionTarget.Rendering += OnCompositionRendering;
                _renderingSubscribed = true;
            }
            catch (Exception ex)
            {
                SafeLog($"[NE25] HeadPoseRendering subscribe EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            try { System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRendering; } catch { /* ignore */ }
            _renderingSubscribed = false;
        }
    }

    private bool _renderingSubscribed;

    // DEAD-14 (D-10, 2026-06-11): the dev/QA hotkeys (Ctrl+Shift+H mock head
    // sweep, Ctrl+Shift+F simulated fault) are armed ONLY when the launch env var
    // SENSEEASE_DEV_HOTKEYS=1 is set — the same opt-in pattern as the WGC bar
    // validation harness. Unset (the alpha-tester default) ⇒ neither hotkey is
    // wired, so a tester can't trip a simulated fault. Read once at construction.
    private static bool DevHotkeysEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("SENSEEASE_DEV_HOTKEYS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>DEAD-14: surface the Ctrl+Shift+F SIMULATED fault in the dashboard
    /// event log, explicitly labeled "simulated" so it can never be mistaken for a
    /// real sensing fault in a field report. Dev-hotkey-only (gated upstream by
    /// <see cref="DevHotkeysEnabled"/>); inserts straight into the bound EventLog
    /// collection on the UI thread (PreviewKeyDown is already UI-thread).</summary>
    private void LogSimulatedFaultEvent(bool faultOn)
    {
        _viewModel.EventLog.Insert(0, new SenseEase.Core.Models.EventLogEntry
        {
            Category = "DevHotkey",
            Message  = faultOn
                ? SenseEase.App.Localization.LocalizationManager.Current.Pick(
                      "[模擬] 開發者快捷鍵觸發模擬感測中斷（非真實故障）。",
                      "[Simulated] Dev hotkey raised a simulated sensing fault (not a real fault).")
                : SenseEase.App.Localization.LocalizationManager.Current.Pick(
                      "[模擬] 開發者快捷鍵已清除模擬感測中斷。",
                      "[Simulated] Dev hotkey cleared the simulated sensing fault."),
        });
    }

    // ── Beta A1 (one-clock motion model) ─────────────────────────────────────
    // (docs/BETA_FEATHERING_COHERENCE_PLAN.md §A1.) This pump is the SINGLE clock
    // for the head-tracked overlay. It funnels the raw sidecar pose through ONE
    // smoothing pass — a single One-Euro filter (_poseFilter) producing one
    // smoothed (cx,cy) per render frame — and hands THAT one value to
    // OverlayWindow.UpdateHeadGazeCenter, from which center, radius and the grid
    // hole are all derived in the same frame on the same time-constant. This
    // replaces the previous 15 ms half-life lerp (which was a SECOND clock,
    // divergent from the radius envelope and the grid EMA → the "multilayer swim"
    // percept during head movement). One-Euro is adaptive: it smooths hard when
    // the head is near-still (kills sensor-jitter "breathing") and opens up on fast
    // head motion (low lag → the corner-reach responsiveness the halo fix bought is
    // preserved as a property of THIS one clock). _displayedCx/Cy now hold the
    // One-Euro output (still the value pushed to the brush, so the recenter logic
    // below is unchanged). Rollback to f381a43: restore the 15 ms half-life lerp
    // here and the separate radius envelope in OverlayWindow.
    private readonly OneEuroFilter2D _poseFilter = new(
        SenseDefaults.OverlayOneEuroFcMin,
        SenseDefaults.OverlayOneEuroBeta,
        SenseDefaults.OverlayOneEuroDCutoff);
    // Beta-1 theme-4 (option B): learns the user's resting head position (velocity-gated) and
    // feeds the pump deviations from it, so an off-axis camera/posture no longer locks the hole
    // off-centre. Runs BEFORE _poseFilter in OnCompositionRendering.
    private readonly NeutralRecenterFilter _neutralRecenter = new();
    private double _displayedCx = 0.5;
    private double _displayedCy = 0.5;
    private DateTime _lastRenderUtc;

    // SIDE-04 (CW-10, 2026-06-11): host-side face-loss grace. The sidecar
    // repeats the last-valid cx/cy at 15 Hz on face loss (face_detected=false),
    // so the sample stream stays fresh and the 2 s staleness fallback never
    // fires — the mask would otherwise park at the last gaze position forever.
    // We track the last time we saw a face-detected sample; once a sample with
    // FaceDetected==false is older than FaceLossGraceMs since the last detected
    // face, we treat the pump as INACTIVE so the existing center-lerp eases the
    // mask back to (0.5, 0.5). C4 prefers this host-side approach over a second
    // Python-side decay (no double-decay). default seeded so a never-detected
    // session is inactive until the first real face.
    private DateTime _lastFaceDetectedUtc = DateTime.MinValue;
    private const double FaceLossGraceMs = 2000.0;

    // M5.1-C PRECONDITION instrumentation (2026-05-29): rolling tick-rate
    // counter so the full-active perf measurement can answer "is the C# 30 Hz
    // timer actually achieving 30 Hz under load, or is starvation causing
    // some of the perceived lag?". Sampled every 5 seconds via SafeLog so
    // the user-side soak run produces a wgc_diag.txt trace alongside the
    // sidecar's own --soak report. Removable after M5.1-C completes.
    private int _headPoseTickCount;
    private DateTime _headPoseTickCountStartUtc;

    /// <summary>M5.1-C iter 2 (2026-05-30): per-render brush update.
    /// CompositionTarget.Rendering fires once per WPF composition pass —
    /// at display refresh rate when the dispatcher isn't saturated, so
    /// brush.Center mutates ~165 Hz on a 165 Hz monitor (vs the previous
    /// ~20 Hz DispatcherTimer which produced visibly stepped motion).
    /// Per-frame logic: pull latest sample → update target → lerp
    /// displayed toward target with a 15 ms half-life → push displayed
    /// to OverlayWindow.UpdateHeadGazeCenter. The interpolation tweens
    /// between the sidecar's 15 Hz sample arrivals so the user perceives
    /// continuous motion.</summary>
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_overlay is null) return;
        try
        {
            var pose = _viewModel.EffectiveHeadPoseReader.ReadLatest();

            // SIDE-04: a non-null pose is necessary but not sufficient. On
            // sustained face loss the sidecar keeps emitting the last-valid
            // cx/cy with FaceDetected==false, so the sample stream stays fresh
            // and the 2 s staleness fallback never fires — the mask would park
            // at the last gaze position forever. Consult FaceDetected directly:
            // a detected face refreshes the grace timer; once a FaceDetected==
            // false sample is older than FaceLossGraceMs since the last detected
            // face we treat the gaze as lost. Per C4 we do NOT pass active=false
            // (that SNAPS the overlay to center); instead we redirect the lerp
            // TARGET to (0.5, 0.5) and let the existing host-side center-lerp
            // below ease the mask back smoothly (no double-decay — host-side
            // only; the sidecar keeps emitting unchanged).
            var now0 = DateTime.UtcNow;
            bool gazeValid;
            if (pose is null)
            {
                gazeValid = false; // sidecar dead / stale / not started
            }
            else if (pose.FaceDetected)
            {
                _lastFaceDetectedUtc = now0;
                gazeValid = true;
            }
            else
            {
                // Face not detected this sample. Honour the gaze during the
                // grace window (brief blink / quick head turn), then ease back.
                double sinceFaceMs = (now0 - _lastFaceDetectedUtc).TotalMilliseconds;
                gazeValid = sinceFaceMs <= FaceLossGraceMs;
            }

            // When the gaze is valid, smooth toward the reported gaze and keep the
            // overlay active. When it is lost, smooth toward center (the One-Euro
            // filter eases the mask back) while keeping the overlay following the
            // smoothed position so the recenter is smooth rather than a snap.
            double targetCx = gazeValid ? pose!.Cx : 0.5;
            double targetCy = gazeValid ? pose!.Cy : 0.5;
            // Beta-1 #3 (clinical-safety): do NOT gate `active` on `pose is not null`. When the
            // sidecar dies / goes stale (ReadLatest == null) with the gaze parked at a corner,
            // the old `pose is not null &&` prefix forced active=false the instant pose went
            // null, which made ApplyHeadPose SNAP the hole to center in one frame (a visible
            // jolt — the exact jump the recenter-grace clause exists to prevent). The target is
            // already 0.5/0.5 when the gaze is lost (above), so keeping `active` true while the
            // displayed center is still off-center lets the One-Euro filter EASE it home; it
            // goes inactive only once centered (then the radius settles to the v0.9 baseline).
            bool active = gazeValid
                          || Math.Abs(_displayedCx - 0.5) > 0.01
                          || Math.Abs(_displayedCy - 0.5) > 0.01;

            // ── Beta A1: ONE smoothing pass — single One-Euro filter ──────────
            // Replaces the previous fixed 15 ms half-life lerp (a second, divergent
            // clock). One-Euro adapts its cutoff to head speed: heavy smoothing
            // near-still (no jitter "breathing"), light smoothing on fast moves
            // (low lag → corner-reach stays responsive). The result IS the one
            // clock — center, radius and grid hole all derive from this single
            // (cx,cy). Sample arrives ~67 ms (15 Hz sidecar); the filter runs at
            // render cadence (~165 Hz) so motion between sample arrivals stays
            // continuous. dtMs is the real inter-frame delta (seconds inside the
            // filter); clamped for the first frame / overflow.
            var now = DateTime.UtcNow;
            double dtMs = (now - _lastRenderUtc).TotalMilliseconds;
            _lastRenderUtc = now;
            if (dtMs <= 0 || dtMs > 100) dtMs = 6.0; // first frame / overflow fallback

            // Beta-1 theme-4 (option B): cancel the constant posture/camera yaw offset BEFORE
            // the One-Euro filter. The sidecar emits absolute-yaw cx with no neutral baseline,
            // so an off-axis camera/seating made the hole "shift and lock" off-centre the moment
            // a face was detected. The recenter learns the resting head position (only while the
            // head is still — so a held off-centre gaze is preserved) and feeds the DEVIATION, so
            // a centred head ⇒ a centred hole. Passes through unchanged while the gaze is lost
            // (target is already 0.5 there).
            (targetCx, targetCy) = _neutralRecenter.Recenter(targetCx, targetCy, dtMs / 1000.0, gazeValid);

            // Beta-1 theme-4 fix (C): head→screen response curve on the recentred deviation — a
            // centre deadzone (kills residual still-head jitter, #3) + expo (fine control near
            // centre, full reach at the edges). Applied per-axis, post-recenter so the deadzone is
            // centred on the user's neutral. Only while the gaze is valid; on loss the target is
            // already the centred 0.5 (deadzone would no-op anyway).
            if (gazeValid)
            {
                targetCx = HeadResponseCurve.Shape(targetCx, SenseDefaults.OverlayHeadDeadzone, SenseDefaults.OverlayHeadExpo);
                targetCy = HeadResponseCurve.Shape(targetCy, SenseDefaults.OverlayHeadDeadzone, SenseDefaults.OverlayHeadExpo);
            }

            (_displayedCx, _displayedCy) =
                _poseFilter.Filter(targetCx, targetCy, dtMs / 1000.0);

            _overlay.UpdateHeadGazeCenter(
                headGazeX: _displayedCx,
                headGazeY: _displayedCy,
                active:    active,
                dtMs:      dtMs);

            // M8 head-tracking sprint: per-frame perf log gated behind
            // AppConfig.HeadTrackingPerfLogEnabled (default false). The two
            // counters proved their value at G6 perf decision but are dead
            // weight in production (5-second log spam, no user value). Flag
            // off → skip the entire 5-second block (no log emission, no
            // sidecar reset). The Interlocked.Increment on _headPoseTickCount
            // (one int add per frame) stays unconditional — it's negligible
            // and lets the flag flip take effect on the next 5-second window
            // when debugging. Mirror the AppConfig.HeadTrackingEnabled flip
            // pattern (code edit + republish) when debugging a perf issue.
            if (Configuration.AppConfig.HeadTrackingPerfLogEnabled)
            {
                _headPoseTickCount++;
                if (_headPoseTickCountStartUtc == default)
                    _headPoseTickCountStartUtc = DateTime.UtcNow;
                var elapsed = DateTime.UtcNow - _headPoseTickCountStartUtc;
                if (elapsed.TotalSeconds >= 5.0)
                {
                    double achievedHz = _headPoseTickCount / elapsed.TotalSeconds;
                    int sidecarSamples = 0;
                    if (_viewModel.EffectiveHeadPoseReader is SenseEase.Infrastructure.Real.SidecarHeadPoseReader sidecar)
                        sidecarSamples = sidecar.ConsumeReceivedSampleCount();
                    double sidecarHz = sidecarSamples / elapsed.TotalSeconds;
                    SafeLog($"[HeadPoseRender] renders={_headPoseTickCount} elapsed={elapsed.TotalSeconds:F2}s achievedHz={achievedHz:F2} (target=monitor refresh) sidecar_received={sidecarSamples} sidecarHz={sidecarHz:F2} (target 15.00)");
                    _headPoseTickCount = 0;
                    _headPoseTickCountStartUtc = DateTime.UtcNow;
                }
            }
        }
        catch
        {
            // Best-effort: a single read failure must not kill the timer.
            // If the reader is in a bad state it'll recover on next tick.
        }
    }

    /// <summary>Beta A1 (one-clock motion model): the SINGLE pose-smoothing pass
    /// for the head-tracked overlay. A 2-axis One-Euro filter (Casiez, Roussel &amp;
    /// Vogel, CHI 2012) — an adaptive low-pass whose cutoff rises with the signal's
    /// speed: <c>fc = FcMin + Beta·|dx/dt|</c>. Near-still ⇒ low cutoff ⇒ heavy
    /// smoothing (rejects the sidecar's sensor jitter, so the feather edge stops
    /// "breathing"); fast head motion ⇒ high cutoff ⇒ light smoothing (low lag, so
    /// corner-reach stays responsive — the property the f381a43 halo fix bought).
    /// This is what lets center, radius and the grid hole all ride ONE clock with a
    /// single asymmetric fast-attack/slow-settle character instead of three
    /// divergent envelopes. Per-axis X/Y share the same parameters; each axis keeps
    /// its own previous value + derivative-EMA. Deterministic given (x,y,dt) — unit
    /// tests drive it through the Filter signature on a fixed cadence.
    ///
    /// <para><c>internal</c> nested type (no outer instance required) so the Beta A1
    /// one-clock convergence test can construct it directly via
    /// <c>MainWindow.OneEuroFilter2D</c> — covered by InternalsVisibleTo
    /// "SenseEase.Tests"; no public-surface widening.</para></summary>
    internal sealed class OneEuroFilter2D
    {
        private readonly double _fcMin;   // minimum cutoff (Hz)
        private readonly double _beta;    // speed coefficient
        private readonly double _dCutoff; // cutoff for the derivative estimate (Hz)
        private bool   _seeded;
        private double _xPrev, _yPrev;    // last filtered position per axis
        private double _dxPrev, _dyPrev;  // last smoothed derivative per axis

        public OneEuroFilter2D(double fcMin, double beta, double dCutoff)
        {
            _fcMin   = fcMin;
            _beta    = beta;
            _dCutoff = dCutoff;
        }

        /// <summary>Smoothing-factor for a 1st-order low-pass given cutoff fc (Hz)
        /// and timestep dt (s): α = 1/(1 + τ/dt), τ = 1/(2π·fc).</summary>
        private static double Alpha(double cutoff, double dt)
        {
            double tau = 1.0 / (2.0 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / dt);
        }

        /// <summary>Filter one (x,y) sample with elapsed <paramref name="dtSeconds"/>
        /// since the previous call. Returns the smoothed (x,y). The first call seeds
        /// state to the raw sample (no lag-in from an arbitrary origin).</summary>
        public (double X, double Y) Filter(double x, double y, double dtSeconds)
        {
            if (dtSeconds <= 0.0) dtSeconds = 1.0 / 165.0; // guard: assume one refresh frame
            if (!_seeded)
            {
                _seeded = true;
                _xPrev = x; _yPrev = y;
                _dxPrev = 0.0; _dyPrev = 0.0;
                return (x, y);
            }

            // 1) Estimate + low-pass the per-axis derivative (speed).
            double dAlpha = Alpha(_dCutoff, dtSeconds);
            double dx = (x - _xPrev) / dtSeconds;
            double dy = (y - _yPrev) / dtSeconds;
            _dxPrev += dAlpha * (dx - _dxPrev);
            _dyPrev += dAlpha * (dy - _dyPrev);

            // 2) Speed-adaptive cutoff per axis, then low-pass the position.
            double cutoffX = _fcMin + _beta * Math.Abs(_dxPrev);
            double cutoffY = _fcMin + _beta * Math.Abs(_dyPrev);
            _xPrev += Alpha(cutoffX, dtSeconds) * (x - _xPrev);
            _yPrev += Alpha(cutoffY, dtSeconds) * (y - _yPrev);
            return (_xPrev, _yPrev);
        }
    }

    /// <summary>overlay-bar-redesign diag-2: best-effort write to
    /// %TEMP%/wgc_diag.txt. Used to instrument the Loaded handler chain
    /// + the tray-spawn path so a silent break is visible without
    /// re-instrumenting.
    ///
    /// <para>Phase 4 (UVHS Playbook clause 5): routed through WgcDiagLog —
    /// adds retry-on-IOException, monotonic [N=xxxxxxx] orthogonal-probe
    /// sequence prefix, and independent loss sink at wgc_diag.lost.log.
    /// Calls remain fire-and-forget from the caller's perspective.</para></summary>
    private static void SafeLog(string msg)
    {
        SenseEase.Infrastructure.Real.WgcDiagLog.Append(
            $"[{DateTime.UtcNow:O}] {msg}");
    }

    private void StartTrayIcon()
    {
        // overlay-bar-redesign diag-2: log every branch of this method so
        // a user-reported "tray completely missing" can be triangulated
        // without code surgery. No production behavior change — all log
        // writes are wrapped in SafeLog's try/catch.
        SafeLog("[StartTrayIcon] entry");

        // Validation harness mutex + emergency-rollback flag, centralised in
        // PopupSurfaceGate. See its docstring for the full truth table.
        string? envVar = Environment.GetEnvironmentVariable(PopupSurfaceGate.ValidationEnvVar);
        bool useNew = UserPreferences.Current.UseNewBarOverlay;
        SafeLog($"[StartTrayIcon] envVar='{envVar ?? "<null>"}' useNewBarOverlay={useNew}");

        if (!PopupSurfaceGate.ShouldSpawnProductionBarAndTray(envVar, useNew))
        {
            SafeLog("[StartTrayIcon] gate REJECTED, returning");
            return;
        }
        SafeLog("[StartTrayIcon] gate passed, constructing TrayIconService");

        _trayIcon = new TrayIconService(
            _viewModel,
            isBarVisible:  () => _persistentBar?.IsVisible ?? false,
            setBarVisible: visible =>
            {
                if (_persistentBar is null) return;
                if (visible) _persistentBar.Show();
                else         _persistentBar.Hide();
            });
        SafeLog("[StartTrayIcon] TrayIconService constructed OK");

        // Task 4: tray left-click opens the notification card for the
        // current tier (user-initiated → bypasses mute per spec).
        _trayIcon.LeftClicked += (_, _) => ShowNotificationCardForCurrentTier(userInitiated: true, allowSetMoveSurface: true);

        // 2026-06-08: single click opens the pop-up card (above); double click opens the
        // main console. The tray service debounces so the single action waits out a
        // possible double-click.
        _trayIcon.DoubleClicked += (_, _) => SurfaceMainWindowFromTray();

        // Task 5: Settings menu opens the dedicated SettingsWindow.
        _trayIcon.SettingsRequested += (_, _) => ShowSettingsWindow();

        _trayIcon.QuitRequested += (_, _) =>
        {
            // Path Z UX iter 5: real-quit path. Set the bypass flag BEFORE
            // calling Shutdown so MainWindow.OnClosing doesn't intercept
            // and hide the window. Without this, Quit from the tray menu
            // would just minimize MainWindow to tray (loop).
            _quittingFromTray = true;
            Application.Current.Shutdown();
        };

        // Path Z UX iter 5: tray menu now also exposes "Open Dashboard" so
        // users who closed the MainWindow to the tray have an explicit
        // path back. Wired below via OpenDashboardRequested.
        _trayIcon.OpenDashboardRequested += (_, _) => SurfaceMainWindowFromTray();

        SafeLog("[StartTrayIcon] exit OK");
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>Confirm before the destructive console Reset. View-layer MessageBox keeps
    /// ResetState/ResetStateCommand testable (no UI in the VM). ResetState is Q4.5-safe
    /// (clears transient shader/FOV personalisation; PRESERVES the earned habituation offset).</summary>
    private void OnConsoleResetClicked(object sender, RoutedEventArgs e)
    {
        if (SenseEase.App.Views.ThemedDialog.Confirm(
                this,
                SenseEase.App.Localization.LocalizationManager.Current.Pick(
                    "確定要重設個人化狀態嗎？\n\n這會清除暫時的個人化（遮罩微調），但保留你已養成的習慣化進度。",
                    "Reset personalisation state?\n\nThis clears transient personalisation (shader tweaks) but keeps your earned habituation progress.")))
            _viewModel.ResetStateCommand.Execute(null);
    }

    /// <summary>Beta-1 D-18/D-19: a comfort-rating star was clicked. Records a MEASURED FMS
    /// data-point (the honest per-user signal that replaces the retired "70%" claim) via the VM,
    /// then shows a brief in-place acknowledgment. The star number rides on the Button's Tag
    /// (1-5). Best-effort + defensive — a malformed Tag or missing VM is a silent no-op so a
    /// rating tap can never throw out of the console.</summary>
    private void OnComfortStarClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b) return;
        if (!int.TryParse(b.Tag?.ToString(), out int stars)) return;

        _viewModel.RecordComfortRating(stars, "manual");

        if (ComfortRatingAck is not null)
        {
            ComfortRatingAck.Text = SenseEase.App.Localization.LocalizationManager.Current.Pick(
                $"已記錄 {stars}★ — 謝謝你的回饋",
                $"Logged {stars}★ — thanks for the feedback");
            ComfortRatingAck.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Open the Event Log in its own window (UI/UX fine-tune 2026-06-07:
    /// relocated from a top-level tab; opened from Settings → 事件記錄). Created on
    /// demand and reused while open; the field is cleared on Closed so the next
    /// request makes a fresh one. Owner = this so it stays grouped with the app
    /// and closes on app exit. DataContext = the shared MainViewModel so the
    /// window's {Binding EventLog} matches the former tab.</summary>
    private EventLogWindow? _eventLogWindow;
    private void ShowEventLogWindow()
    {
        if (_eventLogWindow is null)
        {
            _eventLogWindow = new EventLogWindow(_viewModel) { Owner = this };
            _eventLogWindow.Closed += (_, _) => _eventLogWindow = null;
        }
        _eventLogWindow.Show();
        if (_eventLogWindow.WindowState == WindowState.Minimized)
            _eventLogWindow.WindowState = WindowState.Normal;
        _eventLogWindow.Activate();
    }

    /// <summary>Confirm before clearing the chat (wipes history + the on-disk model trace).</summary>
    private void OnChatClearClicked(object sender, RoutedEventArgs e)
    {
        if (SenseEase.App.Views.ThemedDialog.Confirm(
                this,
                SenseEase.App.Localization.LocalizationManager.Current.Pick(
                    "確定要清除對話紀錄嗎？此動作無法復原。",
                    "Clear the chat history? This cannot be undone.")))
            _viewModel.ChatbotVm.ClearCommand.Execute(null);
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_viewModel.HasVestibularDiagnosis);
        // Alpha-2 addendum: forward the auto-profiling vestibular toggle + Reset wiring so the
        // tray/gear Settings window matches the console Settings tab in the auto-profiling world.
        _settingsWindow.GetVestibularProtection = () => _viewModel.VestibularProtectionEnabled;
        _settingsWindow.SetVestibularProtection = v => _viewModel.VestibularProtectionEnabled = v;
        _settingsWindow.ResetRequested          = () => _viewModel.ResetStateCommand.Execute(null);
        // D-1 (CW-1 step 3): same Graded-exposure wiring as the console 設定 tab so the
        // tray/gear Settings window also flips the engine pause in-session, not just at restart.
        _settingsWindow.GetGradedExposure       = () => _viewModel.GradedExposureEnabled;
        _settingsWindow.SetGradedExposure       = v => _viewModel.GradedExposureEnabled = v;
        _settingsWindow.OpenEventLog            = ShowEventLogWindow;
        // FEAT-08 (CW-10, B1b UI half): same live head-tracking health read as the console
        // 設定 tab so the tray/gear Settings window's status caption is honest too (both hosts).
        _settingsWindow.GetHeadTrackingHealth   = GetHeadTrackingHealth;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.PreferencesChanged += (_, _) => RefreshSettingsDependentSurfaces();
        _settingsWindow.Show();
    }

    /// <summary>FEAT-08 (CW-10, B1b UI half): host-side read of the live head-tracking
    /// health for the Settings status caption (運作中 / 攝影機無畫面 / 已停止). Wired into BOTH
    /// SettingsContent hosts (console 設定 tab + tray/gear SettingsWindow) — the same
    /// both-hosts host-delegate pattern as GradedExposure / Vestibular. Reads the REAL
    /// reader (<see cref="MainViewModel.HeadPoseReader"/>), not the dev mock-sweep
    /// override, matching the reader the Power Save lifecycle drives; <c>Health</c> is a
    /// cheap poll of process/watchdog/parse state. When head-tracking is OFF the reader
    /// is the no-op DefaultHeadPoseReader (not a SidecarHeadPoseReader), so we report
    /// <see cref="SenseEase.Infrastructure.Real.HeadTrackingHealth.Dead"/> — the sidecar
    /// is not running, surfaced as "已停止 / Stopped". Read-only; surfacing only.</summary>
    private SenseEase.Infrastructure.Real.HeadTrackingHealth GetHeadTrackingHealth()
        => _viewModel.HeadPoseReader is SenseEase.Infrastructure.Real.SidecarHeadPoseReader sidecar
            ? sidecar.Health
            : SenseEase.Infrastructure.Real.HeadTrackingHealth.Dead;

    /// <summary>UI Consolidation Sprint M-Phase-1 (2026-05-30): bar repaint
    /// after any persisted Settings change, called by both the tray-menu /
    /// Pop-up Card SettingsWindow path AND the main console's 設定 tab path.
    /// Toggle B may have flipped — repaint immediately rather than wait for
    /// the next StateChanged push. PersistentBarWindow does NOT self-
    /// subscribe to UserPreferences.Changed, so SetTier is the bar's only
    /// path to a Toggle-B repaint outside an engine StateChanged tick.
    /// Phase 5 Step 3 (2026-05-31): the tray repaint that used to live here
    /// is now driven by TrayIconService's own UserPreferences.Changed
    /// subscription (TrayIconService.cs:164), which fires for ANY writer —
    /// SettingsContent, ResetToDefaults, future chatbot intents — not just
    /// the two host-wired paths. Do not re-add _trayIcon?.RefreshAppearance
    /// here without first removing the self-subscription, or every Save()
    /// will trigger two repaints.</summary>
    private void RefreshSettingsDependentSurfaces()
    {
        _persistentBar?.SetTier(_viewModel.VimsTier);
    }

    private void StartPersistentBar()
    {
        // Pop-up revision (Kirby 2026-06-06): the persistent bar is DISABLED — the tray
        // icon is the sole access point (it carries the 5級 ambient + paused/faulted
        // signals) and the card is summoned from the tray as a bottom hover flyout.
        // _persistentBar stays null; every host reference is null-guarded → safe no-ops.
        // The bar code below is retained, dormant; flip AppConfig.PersistentBarEnabled
        // to re-enable. (Static flag, not a const → no unreachable-code warning.)
        if (!AppConfig.PersistentBarEnabled) return;

        // Same gate as StartTrayIcon (centralised in PopupSurfaceGate).
        // Bar + tray spawn together or not at all.
        string? envVar = Environment.GetEnvironmentVariable(PopupSurfaceGate.ValidationEnvVar);
        if (!PopupSurfaceGate.ShouldSpawnProductionBarAndTray(
                envVar, UserPreferences.Current.UseNewBarOverlay))
        {
            return;
        }

        _persistentBar = new PersistentBarWindow();
        _persistentBar.Closed += (_, _) =>
        {
            // Always-on mask (Option II): clear the rect when the bar goes
            // away so the OFI pipeline stops masking a region no longer
            // covered by the bar's pixels.
            _viewModel.SetWgcMonitorExcludeRect(null, null, null, null);
            _persistentBar = null;
        };
        _persistentBar.ContentRendered += (_, _) =>
        {
            if (_persistentBar is null) return;
            var (l, t, w, h) = _persistentBar.GetMonitorRectInPhysicalPixels();
            _viewModel.SetWgcMonitorExcludeRect(l, t, w, h);
            // Initial colour from current state.
            _persistentBar.SetTier(_viewModel.VimsTier);
        };
        // Bar tracks tier changes via StateChanged (raised after each
        // UpdateFromState). VimsTier is also a Set-ref bindable property; we
        // listen via StateChanged so the bar updates on every state push
        // including ones the engine raises outside the dispatcher timer.
        // v0.9.3: StateChanged is raised from WgcSensingProvider's
        // thread-pool timer (OnTick), but everything inside this handler
        // touches WPF DependencyObjects (PersistentBarWindow.SetTier,
        // ShowNotificationCardForCurrentTier → NotificationCardWindow).
        // Without dispatcher marshalling we hit Dispatcher.VerifyAccess
        // throws on any non-UI-thread invocation — the bug that crashed
        // MainWindowSmokeTests under the test host's deterministic timing
        // (v0.9 baseline got a lucky pass; production race was latent).
        _viewModel.StateChanged += (_, st) =>
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnViewModelStateChanged(st));
                return;
            }
            OnViewModelStateChanged(st);
        };
        // Pop-up sprint Task 4: bar click opens the notification card. The
        // click event only fires when the bar is "hovered" (cursor within
        // 200 dip per spec) — click-through behaviour while far away keeps
        // accidental swallowing minimal.
        _persistentBar.Clicked += (_, _) => ShowNotificationCardForCurrentTier(userInitiated: true, allowSetMoveSurface: true);

        // v0.9.1 round-3: HoverSustained removed. Card opens on click or
        // tier-spike auto-show only (per user revision). Hover state remains
        // visual-only (grow + brighten); pop-up buttons + chatbot input live
        // in the card surface that opens on click.

        _persistentBar.Show();
    }

    /// <summary>v0.9.3: extracted body of the StateChanged handler. Must run
    /// on the UI thread (touches WPF DependencyObjects). The handler at the
    /// subscription site marshals here via Dispatcher.BeginInvoke when
    /// called from a non-UI thread (e.g. WgcSensingProvider.OnTick).</summary>
    private void OnViewModelStateChanged(SenseState st)
    {
        _persistentBar?.SetTier(st.VimsTier);

        // Task 5: tier-up auto-expand. When the band crosses out of
        // the calmest band (VeryHigh → any higher-intervention band, the
        // 5級 動態感 equivalent of the legacy grey → amber edge) we treat
        // that as a meaningful state escalation worth the card if Toggle A
        // is ON. Track the previous band so we only fire on the CROSSING
        // edge, not every sample within the new band.
        var newBand = TierColorPalette.BandForTier(st.VimsTier);

        // Mirror tier/pressure into an already-open Card so its title stays
        // in lockstep with the bar; otherwise the Card holds the snapshot
        // taken by ShowNotificationCardForCurrentTier at open time.
        if (_cardWindow is { IsVisible: true })
            _cardWindow.SetContent(newBand, st.VimsTier, st.Pressure);

        if (_lastBand == TierColorPalette.TierBand.VeryHigh
            && newBand != TierColorPalette.TierBand.VeryHigh
            && UserPreferences.Current.UseNewBarOverlay
            && UserPreferences.Current.AutoExpandNotifications)
        {
            ShowNotificationCardForCurrentTier(userInitiated: false);
        }
        _lastBand = newBand;
    }

    // D3 dev/QA: true while Ctrl+Shift+F has forced a liveness fault.
    private bool _devFaultForced;

    /// <summary>D3 liveness fan-out: the SINGLE place the Faulted treatment is
    /// applied to the ambient surfaces, mirroring how OnViewModelStateChanged
    /// fans VimsTier out. Faulted → bar EnterFaultedMode + tray fault verdict;
    /// any healthy/paused state → clear it. The Paused (sleep) treatment stays
    /// owned by ApplyPowerSave — this handler manages only the orthogonal fault
    /// layer. LivenessChanged is raised on the UI thread (the provider posts via
    /// the captured context), but marshal defensively like OnStateChanged in case
    /// a future caller raises it off-thread.</summary>
    private void OnLivenessChanged(object? sender, LivenessState liveness)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLivenessChanged(sender, liveness));
            return;
        }

        if (liveness == LivenessState.Faulted)
        {
            _persistentBar?.EnterFaultedMode();
            _trayIcon?.SetFaultIndicator(true);
        }
        else
        {
            _persistentBar?.ExitFaultedMode();
            _trayIcon?.SetFaultIndicator(false);
        }
    }

    /// <summary>Pop-up sprint Task 4: shows the notification card for the
    /// current tier. <paramref name="userInitiated"/> = true bypasses the
    /// 6-hour mute (user clicks always win per spec). false honours mute and
    /// returns silently if the band is currently muted.</summary>
    /// <summary>M3 7e: MainViewModel signalled that a chatbot proposal has
    /// passed validation and should be surfaced. Opens the Card (auto-
    /// pop per user spec), sets ActiveProposal, and subscribes Accept/
    /// Decline/Defer once.</summary>
    private void OnProposalSurfaceRequested(object? sender, ProfileOverrideProposal proposal)
    {
        ShowNotificationCardForCurrentTier(userInitiated: true);
        if (_cardWindow is null) return;
        WireCardProposalEventsOnce();
        _cardWindow.ActiveProposal = proposal;
    }

    private bool _cardProposalEventsWired;
    private void WireCardProposalEventsOnce()
    {
        if (_cardProposalEventsWired || _cardWindow is null) return;
        _cardWindow.ProposalAccepted += OnCardProposalAccepted;
        _cardWindow.ProposalDeclined += OnCardProposalDeclined;
        _cardWindow.ProposalDeferred += OnCardProposalDeferred;
        _cardWindow.ProposalExpired  += OnCardProposalExpired;
        _cardProposalEventsWired = true;
    }

    private void OnCardProposalAccepted(object? sender, ProfileOverrideProposal p)
    {
        if (_cardWindow is null) return;
        var result = _viewModel.AcceptActiveProposal();
        _cardWindow.ShowProposalApplyResult(result);
    }

    private void OnCardProposalDeclined(object? sender, ProfileOverrideProposal p)
    {
        _viewModel.DeclineActiveProposal();
    }

    private void OnCardProposalDeferred(object? sender, ProfileOverrideProposal p)
    {
        _viewModel.DeferActiveProposal();
    }

    private void OnCardProposalExpired(object? sender, ProfileOverrideProposal p)
    {
        _viewModel.ExpireActiveProposal();
    }

    // ── P5 (propose-confirm): AdaptiveFit set-move card wiring ───────────────
    // Mirrors the chatbot OnCardProposal* handlers but routes to the VM's separate
    // set-move channel. Do NOT touch OnProposalSurfaceRequested / the chatbot wiring.
    private bool _cardSetMoveEventsWired;
    private void WireSetMoveEventsOnce()
    {
        if (_cardSetMoveEventsWired || _cardWindow is null) return;
        _cardWindow.SetMoveAccepted += OnCardSetMoveAccepted;
        _cardWindow.SetMoveDeclined += OnCardSetMoveDeclined;
        _cardWindow.SetMoveDeferred += OnCardSetMoveDeferred;
        _cardWindow.SetMoveExpired  += OnCardSetMoveExpired;
        _cardSetMoveEventsWired = true;
    }

    private void OnCardSetMoveAccepted(object? sender, PendingSetMoveVm m)
    {
        _viewModel.AcceptPendingSetMove();
    }

    private void OnCardSetMoveDeclined(object? sender, PendingSetMoveVm m)
    {
        _viewModel.DeclinePendingSetMove();
    }

    private void OnCardSetMoveDeferred(object? sender, PendingSetMoveVm m)
    {
        _viewModel.DeferPendingSetMove();
    }

    private void OnCardSetMoveExpired(object? sender, PendingSetMoveVm m)
    {
        _viewModel.ExpirePendingSetMove();
    }

    // allowSetMoveSurface: TRUE only when the user themselves opened the card (tray / bar
    // click). It is NOT the same as userInitiated — userInitiated==true ALSO covers the
    // chatbot's OnProposalSurfaceRequested SYSTEM auto-pop (which force-opens the card to
    // show a chatbot proposal). Surfacing the AdaptiveFit set-move proposal on that auto-pop
    // would be a "popup never initiate" violation (a system-decided pop carrying a
    // more-protection suggestion the user didn't open the card to see). So the P5 set-move
    // surface is gated on this stricter flag, passed true ONLY from the two genuine
    // user-open handlers below; every other caller (spike/tier auto-expand, chatbot
    // proposal auto-pop) leaves it at the default false.
    private void ShowNotificationCardForCurrentTier(bool userInitiated, bool allowSetMoveSurface = false)
    {
        var band = TierColorPalette.BandForTier(_viewModel.VimsTier);
        if (!userInitiated && !_cardController.ShouldAutoShow(band)) return;

        if (_cardWindow is null)
        {
            _cardWindow = new NotificationCardWindow();
            // overlay-bar-redesign D3: wire the host-side vestibular lookup
            // so the gear icon can open SettingsWindow with the same
            // diagnosis function used by the tray's settings path.
            _cardWindow.HasVestibularDiagnosis = _viewModel.HasVestibularDiagnosis;
            // overlay-bar-redesign D3: reflect existing power-save state into
            // the freshly-built card so users opening the card while power
            // save is engaged see the override message immediately.
            _cardWindow.IsPowerSaveActive = _powerSaveActive;
            _cardWindow.Closed += (_, _) =>
            {
                _viewModel.SetWgcMonitorExcludeRect(null, null, null, null);
                _cardWindow = null;
                _cardProposalEventsWired = false;
                _cardSetMoveEventsWired = false;
                // Re-engage the bar's mask since closing the card cleared it.
                // Bar may have been hidden by user via tray menu — only
                // re-engage if it's actually visible.
                if (_persistentBar is { IsVisible: true })
                {
                    var (bl, bt, bw, bh) = _persistentBar.GetMonitorRectInPhysicalPixels();
                    _viewModel.SetWgcMonitorExcludeRect(bl, bt, bw, bh);
                }
            };
            _cardWindow.ContentRendered += (_, _) =>
            {
                if (_cardWindow is null) return;
                var (l, t, w, h) = _cardWindow.GetMonitorRectInPhysicalPixels();
                _viewModel.SetWgcMonitorExcludeRect(l, t, w, h);
            };
            // EXCLUDE-RECT ORDERING FIX: ContentRendered pushes the mask once at
            // the card's first paint, but AnchorTopCenter re-anchors on late
            // SizeChanged (SizeToContent=Height settles after first render),
            // moving the card while the WGC exclude rect still points at the old
            // position. Re-push the mask on every LocationChanged *and*
            // SizeChanged so the overlay always excludes the card's CURRENT
            // top-center rect. The Closed handler above still clears the rect on
            // card close — unchanged.
            void PushCurrentCardExcludeRect()
            {
                if (_cardWindow is null) return;
                var (l, t, w, h) = _cardWindow.GetMonitorRectInPhysicalPixels();
                _viewModel.SetWgcMonitorExcludeRect(l, t, w, h);
            }
            _cardWindow.LocationChanged += (_, _) => PushCurrentCardExcludeRect();
            _cardWindow.SizeChanged += (_, _) => PushCurrentCardExcludeRect();
            _cardWindow.EngageClicked += (_, _) =>
            {
                // AF-1: "緩和畫面" must actually, visibly ease the view. Ensure the
                // overlay is showing real WGC-driven protection, THEN apply a
                // deliberate comfort preference that tightens the FOV a notch (D5 —
                // does NOT count as discomfort or erode the earned habituation
                // offset). Card already closes itself on click via its own Hide().
                _viewModel.SetSensingModeToWgc();
                _viewModel.OverlayVisible = true;
                _viewModel.EaseView();
            };
            _cardWindow.MuteClicked += (_, _) =>
            {
                var currentBand = TierColorPalette.BandForTier(_viewModel.VimsTier);
                _cardController.Mute(currentBand);
                System.Diagnostics.Debug.WriteLine(
                    $"[notification-card] muted band={currentBand} until {_cardController.MutedUntilUtc(currentBand):o}");
            };
            _cardWindow.PowerSaveToggled += (_, on) => ApplyPowerSave(on);
            // bar-redesign: the new compact bar's +/- intervention-depth control.
            // +1 = ease MORE (deeper) → reuse the Engage path: ensure WGC + overlay
            // are live, then EaseView() tightens the FOV a notch (D5 deliberate
            // preference, does NOT erode habituation). -1 = ease LESS (widen) →
            // apply a positive deliberate FOV delta through the same engine feedback
            // surface, then refresh VM state so the overlay re-renders at once.
            // The card stays OPEN across +/- so the user can keep nudging.
            _cardWindow.InterventionAdjustRequested += (_, dir) =>
            {
                // Pop-up +/- fine-tune the 12-LEVEL intensity scale (Kirby: "接上 12 level
                // 刻度調節，讓 UX 更直觀"). NudgeIntensityLevel rides the 1..12 baseline floor so
                // BOTH directions visibly change the peripheral dimming and the L-readout moves
                // — fixes the prior FOV-offset mapping where "−" was a no-op at rest. It returns
                // the new displayed level; push it straight to the card's L? readout.
                int level = _viewModel.NudgeIntensityLevel(dir);
                _cardWindow.SetLevel(level);
            };
            // Path Z bug-fix 7: when the user opens Settings via the card's
            // gear icon and changes a preference, repaint the bar immediately
            // so the colour toggle (and any future quick-toggle) feels
            // instant — matching ShowSettingsWindow's tray-menu path. The
            // tray's repaint along this path is handled by TrayIconService's
            // self-subscription to UserPreferences.Changed (Phase 5 Step 3,
            // TrayIconService.cs:164); PersistentBarWindow has no equivalent
            // self-subscription so its repaint must remain host-driven here.
            _cardWindow.OnPreferencesChanged = () =>
            {
                _persistentBar?.SetTier(_viewModel.VimsTier);
            };
            // v0.9.1 round-3: wire the in-card chatbot input through the
            // existing ChatbotViewModel pipeline. Same parser, same engine
            // adjustment hooks, same lexicon fallback. The user gets to
            // "talk to SenseEase" without opening the dashboard. Returns
            // the chatbot's most-recent assistant message so the card can
            // surface a one-line ack beneath the input.
            _cardWindow.SendChatMessage = async text =>
            {
                var vm = _viewModel.ChatbotVm;
                int beforeCount = vm.Messages.Count;
                vm.InputText = text;
                if (vm.SendCommand.CanExecute(null))
                    vm.SendCommand.Execute(null);
                // SendCommand kicks an async task internally; poll briefly
                // (≤6 s) for the assistant reply to land. If it doesn't
                // arrive (LLM down, etc.) the caller surfaces "Applied."
                // since deltas may have applied via lexicon fallback.
                //
                // NE28: a single turn appends MULTIPLE assistant bubbles in
                // order — the reply, then optionally a system-adjustment
                // summary ("已調整…") — and in lexicon mode the reply bubble
                // is added EMPTY and filled incrementally by the fake-stream.
                // Reading Messages[Count-1] therefore risks surfacing the
                // adjustment summary, or a half-streamed fragment, as if it
                // were the reply. Instead wait until the turn fully settles
                // (IsGenerating == false AND the fake-stream has populated the
                // bubble), then return the FIRST assistant bubble added after
                // the user echo: that is the reply, never the trailing summary.
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                    if (vm.IsGenerating) continue;
                    // Scan the bubbles appended this turn. Index beforeCount is
                    // the user echo; the reply is the first assistant bubble
                    // after it that has actually rendered (not the typing
                    // placeholder, not a still-empty fake-stream bubble).
                    for (int j = beforeCount; j < vm.Messages.Count; j++)
                    {
                        var msg = vm.Messages[j];
                        if (msg.Role != ChatRole.Assistant) continue;
                        if (msg.IsTyping) continue;
                        if (string.IsNullOrWhiteSpace(msg.Content)) continue;
                        return msg.Content;
                    }
                }
                return null;
            };
        }
        _cardWindow.SetContent(band, _viewModel.VimsTier, _viewModel.CurrentState.Pressure);
        // HUD-card rework item 2: surface the current intervention level BETWEEN
        // the [ − ] and [ + ] pills (e.g. "L5"). VimsTier is the native level on
        // the same 12-level scale the card clamps to; set it before Show() so the
        // cluster reads [ − ] [ L? ] [ + ] from the first paint.
        _cardWindow.SetLevel(_viewModel.ResolveDisplayedTier(_viewModel.VimsTier));
        // M3 7e: if a chatbot proposal is pending (user clicked Later
        // earlier, or the proposal is fresh from a chatbot turn), wire +
        // re-surface it on this Card open. Filter expired proposals
        // defensively — the VM's expiry watchdog should have cleared them,
        // but a race between watchdog tick (60s) and bar click is possible.
        if (_viewModel.ActiveProposal is { } pending
            && pending.ExpiresAtUtc > DateTime.UtcNow)
        {
            WireCardProposalEventsOnce();
            _cardWindow.ActiveProposal = pending;
        }
        // P5 (propose-confirm): if the AdaptiveFit selector has a pending cross-segment
        // set-move proposal, surface it — but ONLY behind a genuine USER card-open
        // (allowSetMoveSurface, true from tray/bar only). This is STRICTER than userInitiated,
        // which is also true for the chatbot OnProposalSurfaceRequested system auto-pop —
        // surfacing the set-move there would be a "popup never initiate" violation. Filter
        // expired proposals defensively (the engine's 24h TTL clears them on its next tick).
        if (allowSetMoveSurface
            && _viewModel.PendingSetMove is { } sm
            && sm.ExpiresAtUtc > DateTime.UtcNow)
        {
            WireSetMoveEventsOnce();
            _cardWindow.ActiveSetMove = sm;
        }
        // Record whether THIS open was user-initiated (tray / bar / gear) versus
        // an auto-show driven by the tier gate, so the card can adapt its dismiss
        // behaviour accordingly. Must be set before Show() so the value is in
        // place when the card's visibility-change logic runs.
        _cardWindow.UserOpened = userInitiated;
        _cardWindow.Show();
        // Pop-up revision (Kirby 2026-06-06): non-activating hover flyout — do NOT steal
        // focus (the window is ShowActivated=False; the chat face takes focus on click).
    }

    // overlay-bar-redesign D3: process-wide power-save state. The card,
    // bar, tray, and WGC provider all observe this flag so flipping the
    // toggle in the card propagates to every surface.
    private bool _powerSaveActive;

    /// <summary>M2 (Phase 6c): exposed read of the private _powerSaveActive
    /// flag for the VM-side chatbot context builder. Wired into MainViewModel
    /// via SetPowerSaveActiveLookup so ChatbotViewModel can include
    /// <c>power_save_active</c> in the <c>[habituation_state]</c> block
    /// without taking a direct dependency on the View layer.</summary>
    public bool IsPowerSaveActive => _powerSaveActive;

    /// <summary>overlay-bar-redesign D3: applies the Power Save flag across
    /// every dependent surface. Idempotent — a no-op if the state matches
    /// the current value. Pauses/Resumes the WGC capture, drops the bar
    /// into sleep-mode visuals, and flips the tray's status label.</summary>
    private void ApplyPowerSave(bool enabled)
    {
        if (_powerSaveActive == enabled) return;
        _powerSaveActive = enabled;

        // P2 (LOCKED, Wallpaper-Engine-style mode shift): gate the chatbot on
        // ENABLE. While paused the chatbot's SendCommand is disabled (the VM's
        // IsPaused flag drives CanExecute) so no new proposal can be requested
        // mid-pause. The matching un-gate + cross-boundary re-screen lives on
        // the DISABLE (resume) path at the bottom of this method, ordered AFTER
        // the existing resume steps so the snapshot revalidation runs against a
        // fully-resumed pipeline.
        if (enabled) _viewModel.ChatbotVm.IsPaused = true;

        // Drive WGC provider via the VM pass-through. When the VM isn't
        // currently routed to WGC the pass-through is a no-op for ticks,
        // but the paused flag is still updated for the next swap.
        _viewModel.SetWgcPaused(enabled);

        // Path Z follow-up: on Resume, clear the spike detector so the
        // first real frame after a long pause doesn't fire a false spike
        // against the artificial zero baseline accumulated while paused.
        if (!enabled) _viewModel.ResetSpikeDetector();

        // v0.8 hotfix follow-up: Power Save also dismisses the comfort
        // overlay. User mental model: "Power Save = stop all protection".
        // Snapshot/restore mirrors the WGC Pause/Resume contract so a
        // user who manually disabled the overlay before toggling Power
        // Save doesn't see it spontaneously re-enable on Resume.
        if (enabled)
        {
            _overlayVisibleBeforePowerSave = _viewModel.OverlayVisible;
            if (_viewModel.OverlayVisible) _viewModel.OverlayVisible = false;
        }
        else
        {
            if (_overlayVisibleBeforePowerSave && !_viewModel.OverlayVisible)
                _viewModel.OverlayVisible = true;
        }

        if (_persistentBar is not null)
        {
            if (enabled) _persistentBar.EnterSleepMode();
            else          _persistentBar.ExitSleepMode();
        }

        _trayIcon?.SetPowerSaveIndicator(enabled);

        // Phase 5 Step 4 (2026-05-31): Power Save is session-only (no
        // UserPreferences property, no Save() call) so the tray's Step-3
        // self-subscription to UserPreferences.Changed (TrayIconService.cs
        // :164) does NOT fire on this path. Explicit RefreshAppearance is
        // required to close the 50–200 ms gap between WGC resume above
        // (SetWgcPaused(false) at the top of this method) and the first
        // post-resume StateChanged tick. Mirrors the icon-repaint cadence
        // of the SetPowerSaveIndicator label-flip on the line above.
        _trayIcon?.RefreshAppearance(_viewModel.CurrentState);

        // Card may be open and was the source of the toggle — re-render
        // its title/body to reflect the new state. Setting IsPowerSaveActive
        // is no-op when the CheckBox already matches.
        if (_cardWindow is not null)
        {
            _cardWindow.IsPowerSaveActive = enabled;
        }

        // NE25: park/restore the per-frame head-pose pump in lock-step with the
        // sidecar lifecycle below. Entering Power Save stops the sidecar (no
        // fresh poses to pump → no reason to wake at refresh rate); resuming
        // re-subscribes only when head-tracking is enabled. Runs inline on the
        // UI thread (this method is dispatcher-bound), unlike the sidecar
        // Stop/Restart which is offloaded to Task.Run.
        if (AppConfig.HeadTrackingEnabled)
            SetHeadPoseRenderingSubscribed(!enabled);

        // UI Consolidation Sprint Phase 3 (2026-05-30, D-B locked): Power
        // Save also kills the head-tracking sidecar to release the webcam
        // (the OS SCREEN CAPTURE indicator drops — visible privacy proof).
        // On exit, the sidecar restarts iff head-tracking is still enabled,
        // with a ~1-2 s warm-up during which ReadLatest returns null and
        // the FOV mask sits at (0.5, 0.5) center — same fall-back path used
        // when the sidecar dies, so no new shimmer to design.
        //
        // Task.Run for both Stop and Restart because SidecarHeadPoseReader.
        // Stop() waits up to 3 s for the reader task to drain; blocking the
        // UI dispatcher on PowerSave toggle would feel laggy.
        //
        // Phase 3 fix-round-2 (2026-05-30, verification HIGH #D): monotonic
        // epoch comparison preserves user-click order even when the
        // ThreadPool picks Tasks out of order. The SidecarHeadPoseReader's
        // _lifecycleGate serializes execution; this epoch check ensures the
        // final state matches the user's last toggle (not a stale earlier
        // dispatch). Rapid taps coalesce: only the latest dispatched Task
        // does the lifecycle work; earlier Tasks short-circuit at the epoch
        // check and return immediately.
        //
        // Per advisor's 3-quadrant simplification (audit A.5: PowerSave
        // is session-scoped, not persisted), startup PowerSave=off is
        // guaranteed, so we only handle runtime transitions here.
        if (_viewModel.HeadPoseReader is SenseEase.Infrastructure.Real.SidecarHeadPoseReader sidecar)
        {
            long myEpoch = Interlocked.Increment(ref _powerSaveDispatchEpoch);
            bool wantStop = enabled;
            bool canRestart = !enabled && AppConfig.HeadTrackingEnabled;

            Task.Run(() =>
            {
                try
                {
                    // Coalescing guard: only the most-recently dispatched
                    // Task may proceed. If a newer ApplyPowerSave was
                    // dispatched after we were queued, our work is stale —
                    // bail out so the newer Task gets exclusive access to
                    // the lifecycle gate and the final state matches the
                    // user's last click.
                    if (Interlocked.Read(ref _powerSaveDispatchEpoch) != myEpoch) return;

                    if (wantStop)        sidecar.Stop();
                    else if (canRestart) sidecar.Restart();
                }
                catch (Exception ex)
                {
                    // Phase 3 fix-round-2 (2026-05-30, verification HIGH #D
                    // boundary log gap): the previous empty `catch { }`
                    // swallowed ObjectDisposedException from a disposed-gate
                    // race and all other boundary escapes, directly
                    // undermining BLOCKER-3's diagnostic-visibility goal.
                    // SafeLog routes to %TEMP%/wgc_diag.txt — same sink the
                    // SidecarLifecycleLog uses, so PowerSave-lifecycle errors
                    // are co-located with internal sidecar diagnostics for
                    // UVHS post-mortems.
                    SafeLog($"[ApplyPowerSave] sidecar lifecycle threw {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        // P2 (LOCKED, Wallpaper-Engine-style mode shift): on RESUME, un-gate the
        // chatbot THEN re-screen any proposal that was surfaced/pending across the
        // pause boundary. Ordered last so RevalidateActiveProposalAfterResume sees
        // a fully-resumed pipeline (WGC un-paused, overlay restored, sidecar
        // restarting). The revalidate NEVER auto-applies — a Blocked verdict
        // clears the proposal + logs a safety-intercept; otherwise it stays
        // surfaced for the user to re-Accept by hand. Vestibular-UNIFORM: no
        // guard, confirm, warning, or nag (Q3=B).
        if (!enabled)
        {
            _viewModel.ChatbotVm.IsPaused = false;
            _viewModel.RevalidateActiveProposalAfterResume();
        }
    }

    private void MaybeStartWgcValidation()
    {
        string? envVar = Environment.GetEnvironmentVariable(PopupSurfaceGate.ValidationEnvVar);
        if (!PopupSurfaceGate.ShouldSpawnValidationStub(envVar)) return;

        bool maskEnabled = string.Equals(
            Environment.GetEnvironmentVariable("SENSEEASE_WGC_BAR_MASK"),
            "1", StringComparison.Ordinal);

        _wgcBarStub = new BarStubWindow();
        _wgcBarStub.Closed += (_, _) =>
        {
            _wgcBarStub = null;
            _viewModel.SetWgcMonitorExcludeRect(null, null, null, null);
        };
        // Show first so DPI + size are populated; ContentRendered fires after
        // layout so GetMonitorRectInPhysicalPixels has stable values.
        _wgcBarStub.ContentRendered += (_, _) =>
        {
            if (!maskEnabled || _wgcBarStub is null) return;
            var (left, top, w, h) = _wgcBarStub.GetMonitorRectInPhysicalPixels();
            _viewModel.SetWgcMonitorExcludeRect(left, top, w, h);
            System.Diagnostics.Debug.WriteLine(
                $"[WGC validation] mask ENABLED at monitor-rect L={left} T={top} W={w} H={h}");
        };
        _wgcBarStub.Show();
        System.Diagnostics.Debug.WriteLine(
            $"[WGC validation] BarStub shown. mask env var = {(maskEnabled ? "ON" : "OFF")}. " +
            $"Trace: %TEMP%/wgc_ofi_trace.txt");

        // Pop-up sprint Task 7 patch: surface a validation-mode warning
        // banner. Three-level env-var scope detection so the cleanup
        // command shown matches where the var is actually set (Process /
        // User / Machine). Banner is bottom-right, 4s hold + 600ms fade,
        // click-through. Also writes the same diagnostic text to
        // wgc_diag.txt and Debug.WriteLine for permanent records.
        var snap = ValidationModeDiagnostics.ReadCurrent(PopupSurfaceGate.ValidationEnvVar);
        string diagText = ValidationModeDiagnostics.BuildDiagnosticText(
            PopupSurfaceGate.ValidationEnvVar, snap);
        System.Diagnostics.Debug.WriteLine("[VALIDATION MODE]\n" + diagText);
        // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
        SenseEase.Infrastructure.Real.WgcDiagLog.Append(
            "\n--- VALIDATION MODE WARNING ---\n" + diagText + "---");

        _validationBanner = new ValidationModeBanner(diagText);
        _validationBanner.Closed += (_, _) => _validationBanner = null;
        _validationBanner.Show();
    }

    /// <summary>Path Z UX iter 5: intercept window close. When the user has
    /// the "minimize to tray on close" toggle on (default) and the close is
    /// NOT coming from the tray's Quit menu, cancel the close and hide the
    /// window instead. App keeps running with the tray icon as the
    /// surface-of-record; user re-opens MainWindow via the tray's "Open
    /// Dashboard" menu or via the notification card's Open Dashboard button.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Real-quit path: tray-menu Quit set _quittingFromTray before
        // calling Application.Shutdown. Let the close proceed.
        if (_quittingFromTray)
        {
            base.OnClosing(e);
            return;
        }

        // User-preference path: if the user has opted out of tray-resident
        // operation, behave as a normal app and exit on close.
        if (!UserPreferences.Current.MinimizeToTrayOnClose)
        {
            base.OnClosing(e);
            return;
        }

        // Tray-resident path: hide the window, cancel the close.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    /// <summary>Path Z UX iter 5: surface the MainWindow from a tray-menu
    /// "Open Dashboard" click. Mirrors the card's OpenDashboard handler so
    /// both surfaces behave identically.</summary>
    private void SurfaceMainWindowFromTray()
    {
        // Blank-dashboard fix fallback: if the autostart window is still parked off-screen
        // (ContentRendered hasn't fired yet, or an off-screen render was culled), bring it
        // back on-screen now + clear the park flag so ContentRendered won't later re-hide it.
        // Becoming visible on a real monitor forces the first render → never blank.
        if (_trayStartupPark)
        {
            _trayStartupPark = false;
            UnparkOffscreenWindow(hideAfter: false);
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Topmost dance — bring above PersistentBarWindow (Topmost=true)
        // without permanently pinning the dashboard.
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Blank-dashboard fix: bring the off-screen-parked autostart window back on-
    /// screen (centered on the work area) and restore taskbar/activation. Called from
    /// ContentRendered (<paramref name="hideAfter"/>:true — render confirmed, hide until the
    /// user opens it) and as a fallback from SurfaceMainWindowFromTray (false — surface now).</summary>
    private void UnparkOffscreenWindow(bool hideAfter)
    {
        ShowInTaskbar = true;
        ShowActivated = true;
        var wa = SystemParameters.WorkArea;
        double w = ActualWidth  > 0 ? ActualWidth  : (double.IsNaN(Width)  ? 0 : Width);
        double h = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 0 : Height);
        Left = wa.Left + System.Math.Max(0, (wa.Width  - w) / 2);
        Top  = wa.Top  + System.Math.Max(0, (wa.Height - h) / 2);
        if (hideAfter) Hide();
    }

    // B2/B4: Recreate overlay if null or already unloaded; sync OverlayVisible on external close.
    private void OnOverlayVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            if (_overlay == null || !_overlay.IsLoaded)
            {
                _overlay = new OverlayWindow
                {
                    // Read-only consumer of MainViewModel.HasVestibularDiagnosis.
                    // D-10 (2026-06-11): the escape hatch this once guarded was
                    // removed entirely (OverlayWindow.TriggerEscapeHatch deleted),
                    // so the vestibular force-disable is now vacuous. The lookup is
                    // still wired as the overlay's read-only vestibular-status seam
                    // for any future vestibular-sensitive surface. HabituationEngine
                    // + HabituationProfileSnapshot are NOT modified — we only consume
                    // the existing API.
                    HasVestibularDiagnosis = _viewModel.HasVestibularDiagnosis,
                };
                _overlay.Closed += OnOverlayClosed;   // B4: keep VM in sync
            }

            _overlay.Show();
            // ABCD test fix: claim the top of the topmost band the moment the overlay
            // shows — WPF asserts Topmost only once at Show, and a foreign topmost
            // window (game/launcher/consent prompt) can sit above it from frame one.
            _overlay.ReassertTopmost();
            _overlay.ApplyState(_viewModel.CurrentState);
        }
        else
        {
            _overlay?.Hide();
        }
    }

    // B4: User closes overlay window externally → sync ViewModel flag.
    private void OnOverlayClosed(object? sender, System.EventArgs e)
    {
        _overlay = null;                       // B2: force recreation next time
        _viewModel.OverlayVisible = false;    // B4: keep checkbox/button in sync
    }

    // v0.9.3: visibility internal so the cross-thread regression test in
    // SenseEase.Tests can call this directly from a thread-pool task (the
    // production trigger path is WgcSensingProvider.OnTick).
    internal void OnStateChanged(object? sender, SenseState state)
    {
        // v0.9.3: this is the SECOND cross-thread anti-pattern fixed in the
        // hotfix. StateChanged fires from WgcSensingProvider's thread-pool
        // timer; OverlayWindow.ApplyState touches WPF DependencyObjects
        // (Panel.InternalChildren) which require UI-thread access. Without
        // the dispatcher marshal we hit Dispatcher.VerifyAccess throws.
        // Sibling fix: the StateChanged → PersistentBar lambda at
        // StartPersistentBar gets the same treatment.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStateChanged(sender, state));
            return;
        }

        if (_overlay is { IsVisible: true })
        {
            // Issue #4 — Always-on overlay: floor the displayed tier to the user's
            // configured baseline. Natural higher tiers (real pressure) still win.
            // Mutate the engine-shared SenseState in place — the engine never reads
            // VimsTier as input, so this is a forward-only display override.
            int original = state.VimsTier;
            int displayed = _viewModel.ResolveDisplayedTier(original);
            double originalOverride = state.MaskPeakAlphaOverride;
            bool originalSteady = state.PersistentBaselineSteady;
            state.VimsTier = displayed;
            // When the floor ELEVATES the tier, the engine's MaskPeakAlphaOverride was resolved
            // for the raw (calm ≈ L1 ⇒ ~0) tier and would blank the always-on mask in
            // ApplyFovMask. Re-resolve it for the displayed tier so the baseline dimming shows.
            //
            // Kirby round-3 (item 3): 保持遮罩開啟 renders the CHOSEN baseline tier steadily
            // whenever the baseline is AT OR ABOVE the live/natural tier (baselineTier >=
            // naturalTier) — NOT only when it strictly elevates. The "==" case matters too: if
            // the live tier merely MEETS the baseline, live OFI/pressure noise would still pulse
            // the grid/mask within that tier. Flag the state so OverlayWindow renders the baseline
            // WITHOUT live-sensing jitter (constant intensity, no mask cross-fade chase). Only a
            // genuine spike — the live tier EXCEEDS the baseline — leaves the flag false so normal
            // live smoothing takes over (protection never weakens); it returns to the steady
            // baseline (riding the engine VimsTier debounce hysteresis) when the spike subsides.
            bool baselineSteady = _viewModel.PersistentOverlayEnabled
                                  && _viewModel.PersistentOverlayBaselineTier >= original;
            if (displayed != original)
                state.MaskPeakAlphaOverride = _viewModel.ResolveDisplayedMaskOverride(displayed, originalOverride);
            if (baselineSteady)
                state.PersistentBaselineSteady = true;
            // Round-10 one-clock fix: state.HeadGazeX/Y here is the RAW un-One-Euro'd sidecar
            // sample (MainViewModel.UpdateFromState). While the per-frame pump is subscribed
            // its One-Euro output is the single pose authority — tell the overlay to ignore
            // the raw pose on this path (the dual-writer ~15 Hz tremble). Refreshed every
            // tick so it tracks pump subscribe/unsubscribe regardless of lifecycle order.
            _overlay.PoseOwnedByPump = _renderingSubscribed;
            _overlay.ApplyState(state);
            // Restore so any other subscriber (or the engine's next-tick logging)
            // sees the un-elevated values.
            state.VimsTier               = original;
            state.MaskPeakAlphaOverride  = originalOverride;
            state.PersistentBaselineSteady = originalSteady;
        }
    }

    // ── Eye-Protection live chart rendering ───────────────────────────────────
    private const double ChartYMax = 200.0;
    private const double ChartTriggerLine = SenseDefaults.ChartTriggerLine; // F2: single-source (== TriggerPressure)
    private System.Windows.Shapes.Polyline? _ofiPolyline;
    private System.Windows.Shapes.Polyline? _pressurePolyline;
    private System.Windows.Shapes.Line? _triggerLine;
    private bool _chartDirty;
    private System.Windows.Threading.DispatcherTimer? _chartTimer;

    private void EnsureChartInitialized()
    {
        if (PressureChartCanvas == null || _ofiPolyline != null) return;
        _ofiPolyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x7B, 0x8D, 0xB0)),
            StrokeThickness = 1.5,
        };
        _pressurePolyline = new System.Windows.Shapes.Polyline
        {
            Stroke = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x02, 0x4A, 0xD8)),
            StrokeThickness = 2.0,
        };
        _triggerLine = new System.Windows.Shapes.Line
        {
            Stroke = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xE5, 0x47, 0x47)),
            StrokeThickness = 1.0,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 },
        };
        PressureChartCanvas.Children.Add(_triggerLine);
        PressureChartCanvas.Children.Add(_ofiPolyline);
        PressureChartCanvas.Children.Add(_pressurePolyline);
        PressureChartCanvas.SizeChanged += (_, _) => RedrawChart();
    }

    private void RedrawChart()
    {
        if (PressureChartCanvas == null || _ofiPolyline == null || _pressurePolyline == null || _triggerLine == null) return;
        if (DataContext is not SenseEase.App.ViewModels.MainViewModel vm) return;
        double w = PressureChartCanvas.ActualWidth;
        double h = PressureChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double triggerY = h - (ChartTriggerLine / ChartYMax * h);
        _triggerLine.X1 = 0; _triggerLine.X2 = w;
        _triggerLine.Y1 = triggerY; _triggerLine.Y2 = triggerY;
        // In-place mutation: reuse existing PointCollections to avoid per-tick allocation.
        FillChartPoints(vm.OfiHistory,      _ofiPolyline.Points,      w, h);
        FillChartPoints(vm.PressureHistory, _pressurePolyline.Points, w, h);
    }

    private static void FillChartPoints(
        System.Collections.Generic.IReadOnlyList<double> samples,
        System.Windows.Media.PointCollection target,
        double w, double h)
    {
        int n = samples.Count;
        // Grow or shrink the collection to exactly n slots.
        while (target.Count < n) target.Add(new System.Windows.Point(0, 0));
        while (target.Count > n) target.RemoveAt(target.Count - 1);
        if (n < 2) return;
        double dx = w / Math.Max(1, n - 1);
        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(samples[i], 0, ChartYMax);
            double y = h - (v / ChartYMax * h);
            // Point is a struct — indexer assignment is a value copy, no heap allocation.
            target[i] = new System.Windows.Point(i * dx, y);
        }
    }
}
