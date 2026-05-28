using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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

        // M3 7e visual verification: Ctrl+Shift+P injects a test proposal
        // into the card so the user can verify the new section's layout
        // without the full chatbot pipeline being wired. Production wiring
        // (chatbot reply → parser → processor → card.ActiveProposal) lands
        // in a follow-up commit after visual sign-off.
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.P
                && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
                && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                InjectTestProposalForVisualVerification();
                e.Handled = true;
            }
        };

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

        // TODO(lux-removal): user decision 2026-05-27 — ambient-light comfort
        // pipeline disabled because RGB-camera-based lux estimation was proven
        // unreliable in field testing. Code left in place (WireLuxComfort,
        // LuxComfortController, WebcamLuxProvider, LightSensorProvider,
        // MockAmbientLightProvider, LuxComfortMapper, IAmbientLightProvider)
        // so a future sprint can revive with a real OS ALS provider; the
        // entry point is gated here so no resources get spent at startup.
        // To revive: uncomment the WireLuxComfort call below.
        //
        // SourceInitialized += (_, _) =>
        // {
        //     if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
        //     {
        //         IntPtr hwnd = new WindowInteropHelper(this).Handle;
        //         vm.WireLuxComfort(hwnd);
        //     }
        // };
        Closed += (_, _) =>
        {
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
            {
                vm.DisposeServices();
                vm.LuxController?.Shutdown();
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
    }

    /// <summary>overlay-bar-redesign diag-2: best-effort append to
    /// %TEMP%/wgc_diag.txt. Used to instrument the Loaded handler chain
    /// + the tray-spawn path so a silent break is visible without
    /// re-instrumenting. File IO is fire-and-forget; a log failure must
    /// never break the spawn chain it's logging.</summary>
    private static void SafeLog(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "wgc_diag.txt"),
                $"[{DateTime.UtcNow:O}] {msg}\n");
        }
        catch { /* fire-and-forget */ }
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
        _settingsWindow.PreferencesChanged += (_, _) =>
        {
            // Toggle B may have flipped — repaint bar/tray immediately
            // rather than wait for the next StateChanged push. Tray's
            // ApplyState reads UserPreferences.Current on every state
            // event, so triggering a state event is the simplest path:
            // SetTier on the bar and call OnStateChanged-equivalent on
            // the tray via a synthetic re-push.
            _persistentBar?.SetTier(_viewModel.VimsTier);
            // For the tray, we route a fake state by re-raising
            // StateChanged from the VM — but that affects other listeners
            // too. Cleaner: expose a Refresh method on the tray.
            _trayIcon?.RefreshAppearance(_viewModel.CurrentState);
        };
        _settingsWindow.Show();
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
        _viewModel.StateChanged += (_, st) =>
        {
            _persistentBar?.SetTier(st.VimsTier);

            // Task 5: tier-up auto-expand. When the band crosses
            // grey → amber (≤5 → ≥6) we treat that as a meaningful
            // state escalation worth the card if Toggle A is ON. Track
            // the previous band so we only fire on the CROSSING edge,
            // not every sample within the new band.
            var newBand = TierColorPalette.BandForTier(st.VimsTier);
            if (_lastBand == TierColorPalette.TierBand.Grey
                && newBand != TierColorPalette.TierBand.Grey
                && UserPreferences.Current.UseNewBarOverlay
                && UserPreferences.Current.AutoExpandNotifications)
            {
                ShowNotificationCardForCurrentTier(userInitiated: false);
            }
            _lastBand = newBand;
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

    /// <summary>Pop-up sprint Task 4: shows the notification card for the
    /// current tier. <paramref name="userInitiated"/> = true bypasses the
    /// 6-hour mute (user clicks always win per spec). false honours mute and
    /// returns silently if the band is currently muted.</summary>
    /// <summary>M3 7e visual verification helper: builds a fake
    /// AdjustSensitivity proposal and pushes it through the card's
    /// proposal pipeline so the new section's layout is visible without
    /// requiring an end-to-end chatbot round-trip. Bound to Ctrl+Shift+P
    /// in the dashboard. Removed once 7e is fully wired through the
    /// chatbot pipeline.</summary>
    private void InjectTestProposalForVisualVerification()
    {
        var fakeProposal = new ProfileOverrideProposal(
            Intent: OverrideIntent.AdjustSensitivity,
            TargetField: "personal_pressure_scale",
            ProposedValue: 1.1,
            Rationale: "(test proposal — Ctrl+Shift+P) 觀察到過去 5 天 tier 維持 6-7，建議稍微調整 sensitivity。",
            ExpiresAtUtc: DateTime.UtcNow.AddHours(4));

        // Make sure the card window exists.
        ShowNotificationCardForCurrentTier(userInitiated: true);
        if (_cardWindow is null) return;

        _cardWindow.ActiveProposal = fakeProposal;
        // Visual-only: wire the three events to a no-op stub here. The
        // production wiring lands in the follow-up commit; this stub just
        // clears the section so the user can re-trigger Ctrl+Shift+P.
        _cardWindow.ProposalAccepted -= OnTestProposalAccepted;
        _cardWindow.ProposalDeclined -= OnTestProposalDeclined;
        _cardWindow.ProposalDeferred -= OnTestProposalDeferred;
        _cardWindow.ProposalAccepted += OnTestProposalAccepted;
        _cardWindow.ProposalDeclined += OnTestProposalDeclined;
        _cardWindow.ProposalDeferred += OnTestProposalDeferred;
    }

    private void OnTestProposalAccepted(object? sender, ProfileOverrideProposal p)
    {
        if (_cardWindow is null) return;
        // Pretend the apply succeeded so the section clears. The real wiring
        // calls HabituationOverrideApplicator.ApplyAccepted here.
        _cardWindow.ShowProposalApplyResult(ApplyResult.Success());
    }

    private void OnTestProposalDeclined(object? sender, ProfileOverrideProposal p)
    {
        // Real wiring calls applicator.RecordDeclined. Visual-only stub: no-op.
    }

    private void OnTestProposalDeferred(object? sender, ProfileOverrideProposal p)
    {
        // Real wiring keeps the proposal pending in memory + hints on the bar.
        // Visual-only stub: no-op (proposal already cleared by card-side click handler).
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
            // gear icon and changes a preference, repaint the bar/tray
            // immediately so the colour toggle (and any future quick-toggle)
            // feels instant — matching ShowSettingsWindow's tray-menu path.
            _cardWindow.OnPreferencesChanged = () =>
            {
                _persistentBar?.SetTier(_viewModel.VimsTier);
                _trayIcon?.RefreshAppearance(_viewModel.CurrentState);
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

        // Card may be open and was the source of the toggle — re-render
        // its title/body to reflect the new state. Setting IsPowerSaveActive
        // is no-op when the CheckBox already matches.
        if (_cardWindow is not null)
        {
            _cardWindow.IsPowerSaveActive = enabled;
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
        try
        {
            string diagFile = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "wgc_diag.txt");
            System.IO.File.AppendAllText(diagFile,
                "\n--- VALIDATION MODE WARNING ---\n" + diagText + "---\n");
        }
        catch { /* best-effort */ }

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
                _overlay = new OverlayWindow();
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

    private void OnStateChanged(object? sender, SenseState state)
    {
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
