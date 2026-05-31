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
    // Initialised to Grey (the default startup state).
    private TierColorPalette.TierBand _lastBand = TierColorPalette.TierBand.Grey;

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
        SettingsTabContent.PreferencesChanged    += (_, _) => RefreshSettingsDependentSurfaces();

        _viewModel.OverlayVisibilityChanged += OnOverlayVisibilityChanged;
        _viewModel.StateChanged             += OnStateChanged;

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

                        if (MainTabs is not null && MainTabs.Items.Count > 2)
                            MainTabs.SelectedIndex = 2;
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
                    if (_chartDirty) { _chartDirty = false; RedrawChart(); }
                };
                _chartTimer.Start();
            }
        };

        // Path Z UX iter 5: when launched with --minimized (set by the
        // AutoStartRegistry entry on Windows login), don't show the
        // MainWindow at all. The Loaded event still fires (so tray + bar
        // + sensing all initialise), but the window itself stays hidden.
        // User surfaces it later via tray "Open Dashboard" or the
        // notification card. Subscribed BEFORE the show-side wiring so
        // the Hide() lands before any deferred render passes.
        SourceInitialized += (_, _) =>
        {
            if (App.StartMinimizedToTray)
            {
                // ShowInTaskbar stays true so once the user does open the
                // dashboard, it appears in Alt-Tab normally. Hide() just
                // suppresses the initial visual; no taskbar flicker either.
                Hide();
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
        Activated += (_, _) => _persistentBar?.ReassertTopmost();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal || WindowState == WindowState.Maximized)
                _persistentBar?.ReassertTopmost();
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

        // M5 head-tracking sprint: Ctrl+Shift+H triggers the developer
        // mock head-pose sweep. 20-second deterministic pattern (horizontal
        // → vertical → circular → center hold). Intentionally independent
        // of AppConfig.HeadTrackingEnabled — the hotkey is a developer/QA
        // surface used during G4 UVHS to prove the wiring end-to-end on
        // machines without a working webcam. Second press while a sweep
        // is in flight is a no-op (ViewModel.StartMockHeadPoseSweep returns
        // immediately if _overrideReader is already non-null).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.H &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift)
            {
                _viewModel.StartMockHeadPoseSweep();
                e.Handled = true;
                return;
            }

            // M5.1-B escape hatch: double-tap Ctrl (no other keys) within
            // 400 ms triggers OverlayWindow.TriggerEscapeHatch(). Detection
            // strategy: track Ctrl KeyDown events that arrive cleanly (no
            // other key being held). On the second clean Ctrl KeyDown
            // within the window, fire. Reset on any non-Ctrl KeyDown so
            // chord typing (Ctrl+C, Ctrl+S, etc.) doesn't poison the state.
            //
            // M8 escape × vestibular guard (defensive layer): even before
            // we look at AppConfig.HeadTrackingEscapeHatchEnabled, no-op
            // entirely for vestibular-diagnosed users. The OverlayWindow
            // also checks via HasVestibularDiagnosis (set on construction)
            // — this site is a belt-and-braces second check so a forced-on
            // AppConfig + JSON edit can't bypass the protection.
            if (_viewModel.HasVestibularDiagnosis())
            {
                _lastCtrlTapUtc = default;
                return;
            }
            bool isCtrl = e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl;
            if (isCtrl)
            {
                // Ignore auto-repeat (held Ctrl spams KeyDown).
                if (e.IsRepeat) return;

                var now = DateTime.UtcNow;
                var sinceLast = now - _lastCtrlTapUtc;
                if (sinceLast.TotalMilliseconds <= 400)
                {
                    // Double-tap. Trigger and reset.
                    _lastCtrlTapUtc = default;
                    _overlay?.TriggerEscapeHatch();
                }
                else
                {
                    _lastCtrlTapUtc = now;
                }
            }
            else
            {
                // Any other key resets the double-tap window so Ctrl+X
                // doesn't accidentally fire the escape hatch.
                _lastCtrlTapUtc = default;
            }
        };

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
            SafeLog("[Loaded#5] HeadPoseRendering subscribe");
            try
            {
                System.Windows.Media.CompositionTarget.Rendering += OnCompositionRendering;
                _renderingSubscribed = true;
                SafeLog("[Loaded#5] HeadPoseRendering subscribe exit OK");
            }
            catch (Exception ex)
            {
                SafeLog($"[Loaded#5] HeadPoseRendering subscribe EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        };
        Unloaded += (_, _) =>
        {
            if (_renderingSubscribed)
            {
                try { System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRendering; } catch { /* ignore */ }
                _renderingSubscribed = false;
            }
        };
    }

    private bool _renderingSubscribed;
    private DateTime _lastCtrlTapUtc;   // M5.1-B escape-hatch double-tap timestamp
    // Interpolated brush position. _displayedCx/Cy is what's pushed to the
    // brush; _targetCx/Cy is the latest sample reading (from the reader).
    // Each render frame lerps displayed toward target.
    private double _displayedCx = 0.5;
    private double _displayedCy = 0.5;
    private DateTime _lastRenderUtc;

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
            double targetCx = pose?.Cx ?? 0.5;
            double targetCy = pose?.Cy ?? 0.5;
            bool active = pose is not null;

            // Time-based lerp toward target. 15 ms half-life means we
            // close half the remaining distance every 15 ms, ~30 ms to
            // get within 25 %, ~50 ms to get within ~10 %. Sample arrives
            // every ~67 ms (15 Hz sidecar), so we always settle before
            // the next sample arrives without adding visible latency.
            var now = DateTime.UtcNow;
            double dtMs = (now - _lastRenderUtc).TotalMilliseconds;
            _lastRenderUtc = now;
            if (dtMs <= 0 || dtMs > 100) dtMs = 6.0; // first frame / overflow fallback

            const double HalfLifeMs = 15.0;
            double factor = 1.0 - Math.Exp(-dtMs * Math.Log(2.0) / HalfLifeMs);
            _displayedCx += factor * (targetCx - _displayedCx);
            _displayedCy += factor * (targetCy - _displayedCy);

            _overlay.UpdateHeadGazeCenter(
                headGazeX: _displayedCx,
                headGazeY: _displayedCy,
                active:    active);

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
        _trayIcon.LeftClicked += (_, _) => ShowNotificationCardForCurrentTier(userInitiated: true);

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

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_viewModel.HasVestibularDiagnosis);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.PreferencesChanged += (_, _) => RefreshSettingsDependentSurfaces();
        _settingsWindow.Show();
    }

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
        _persistentBar.Clicked += (_, _) => ShowNotificationCardForCurrentTier(userInitiated: true);

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

        // Task 5: tier-up auto-expand. When the band crosses
        // grey → amber (≤5 → ≥6) we treat that as a meaningful
        // state escalation worth the card if Toggle A is ON. Track
        // the previous band so we only fire on the CROSSING edge,
        // not every sample within the new band.
        var newBand = TierColorPalette.BandForTier(st.VimsTier);

        // Mirror tier/pressure into an already-open Card so its title stays
        // in lockstep with the bar; otherwise the Card holds the snapshot
        // taken by ShowNotificationCardForCurrentTier at open time.
        if (_cardWindow is { IsVisible: true })
            _cardWindow.SetContent(newBand, st.VimsTier, st.Pressure);

        if (_lastBand == TierColorPalette.TierBand.Grey
            && newBand != TierColorPalette.TierBand.Grey
            && UserPreferences.Current.UseNewBarOverlay
            && UserPreferences.Current.AutoExpandNotifications)
        {
            ShowNotificationCardForCurrentTier(userInitiated: false);
        }
        _lastBand = newBand;
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

    private void ShowNotificationCardForCurrentTier(bool userInitiated)
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
            _cardWindow.EngageClicked += (_, _) =>
            {
                // Path Z UX iter 2: 啟動保護 = enforce protective intervention.
                // (1) ensure sensing is on the real-measurement provider (WGC),
                // (2) surface the comfort overlay so the user gets immediate
                // visual mitigation. Card already closes itself on click via
                // its own Hide().
                _viewModel.SetSensingModeToWgc();
                _viewModel.OverlayVisible = true;
            };
            _cardWindow.MuteClicked += (_, _) =>
            {
                var currentBand = TierColorPalette.BandForTier(_viewModel.VimsTier);
                _cardController.Mute(currentBand);
                System.Diagnostics.Debug.WriteLine(
                    $"[notification-card] muted band={currentBand} until {_cardController.MutedUntilUtc(currentBand):o}");
            };
            _cardWindow.PowerSaveToggled += (_, on) => ApplyPowerSave(on);
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
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                    if (vm.Messages.Count > beforeCount + 1)
                    {
                        var last = vm.Messages[vm.Messages.Count - 1];
                        if (last.Role == ChatRole.Assistant)
                            return last.Content;
                    }
                }
                return null;
            };
        }
        _cardWindow.SetContent(band, _viewModel.VimsTier, _viewModel.CurrentState.Pressure);
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
        _cardWindow.Show();
        _cardWindow.Activate();
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
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Topmost dance — bring above PersistentBarWindow (Topmost=true)
        // without permanently pinning the dashboard.
        Topmost = true;
        Topmost = false;
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
                    // M8 escape × vestibular guard: read-only consumer of
                    // MainViewModel.HasVestibularDiagnosis. When the user's
                    // calibration recorded a vestibular diagnosis,
                    // OverlayWindow.TriggerEscapeHatch no-ops even if the
                    // AppConfig flag is true. Settings UI also greys out
                    // the toggle; this is the defensive second layer.
                    // HabituationEngine + HabituationProfileSnapshot are
                    // NOT modified — we only consume the existing API.
                    HasVestibularDiagnosis = _viewModel.HasVestibularDiagnosis,
                };
                _overlay.Closed += OnOverlayClosed;   // B4: keep VM in sync
            }

            _overlay.Show();
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
            state.VimsTier = _viewModel.ResolveDisplayedTier(original);
            _overlay.ApplyState(state);
            // Restore so any other subscriber (or the engine's next-tick logging)
            // sees the un-elevated value.
            state.VimsTier = original;
        }
    }

    // ── Eye-Protection live chart rendering ───────────────────────────────────
    private const double ChartYMax = 200.0;
    private const double ChartTriggerLine = 85.0;
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
