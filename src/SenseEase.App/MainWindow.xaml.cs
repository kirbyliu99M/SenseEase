using System;
using System.Windows;
using System.Windows.Interop;
using SenseEase.App.Configuration;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using SenseEase.App.Views;
using SenseEase.Core.Models;

namespace SenseEase.App;

/// <summary>Dashboard window. Hosts the view model and manages the overlay window.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private OverlayWindow? _overlay;
    private SpikePopupWindow? _spikePopup;
    // Pop-up sprint Task 1: BarStub for WGC mask validation. Spawned only when
    // env var SENSEEASE_WGC_BAR_VALIDATION=1 is set at app launch. See OnLoaded.
    private BarStubWindow? _wgcBarStub;
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

        SourceInitialized += (_, _) =>
        {
            if (DataContext is SenseEase.App.ViewModels.MainViewModel vm)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                vm.WireLuxComfort(hwnd);
            }
        };
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
        // Stop the 15 Hz chart timer so the DispatcherTimer + VM ref don't pin the
        // window after Close (kept the Tick closure alive otherwise).
        Closed += (_, _) => _chartTimer?.Stop();
        Closed += (_, _) => _wgcBarStub?.Close();
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
        Loaded += (_, _) => MaybeStartWgcValidation();
        // Pop-up sprint Task 2: spawn the persistent ambient bar AFTER the WGC
        // validation block so a validation run isn't accidentally fighting the
        // production bar over the same monitor rect. In a validation run
        // (SENSEEASE_WGC_BAR_VALIDATION=1) the BarStub owns the mask + screen
        // position; in a production run the BarStub never spawns and the
        // persistent bar owns both.
        Loaded += (_, _) => StartPersistentBar();
        // Pop-up sprint Task 3: spawn tray icon AFTER the bar so the tray's
        // initial "toggle bar visibility" label correctly reflects the bar's
        // initial visible state. Same validation-env-var mutex as the bar.
        Loaded += (_, _) => StartTrayIcon();
    }

    private void StartTrayIcon()
    {
        // Validation harness mutex + emergency-rollback flag, centralised in
        // PopupSurfaceGate. See its docstring for the full truth table.
        string? envVar = Environment.GetEnvironmentVariable(PopupSurfaceGate.ValidationEnvVar);
        if (!PopupSurfaceGate.ShouldSpawnProductionBarAndTray(
                envVar, UserPreferences.Current.UseNewBarOverlay))
        {
            return;
        }

        _trayIcon = new TrayIconService(
            _viewModel,
            isBarVisible:  () => _persistentBar?.IsVisible ?? false,
            setBarVisible: visible =>
            {
                if (_persistentBar is null) return;
                if (visible) _persistentBar.Show();
                else         _persistentBar.Hide();
            });

        // Task 4: tray left-click opens the notification card for the
        // current tier (user-initiated → bypasses mute per spec).
        _trayIcon.LeftClicked += (_, _) => ShowNotificationCardForCurrentTier(userInitiated: true);

        // Task 5: Settings menu opens the dedicated SettingsWindow.
        _trayIcon.SettingsRequested += (_, _) => ShowSettingsWindow();

        _trayIcon.QuitRequested += (_, _) => Application.Current.Shutdown();
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
        _persistentBar.Show();
    }

    /// <summary>Pop-up sprint Task 4: shows the notification card for the
    /// current tier. <paramref name="userInitiated"/> = true bypasses the
    /// 6-hour mute (user clicks always win per spec). false honours mute and
    /// returns silently if the band is currently muted.</summary>
    private void ShowNotificationCardForCurrentTier(bool userInitiated)
    {
        var band = TierColorPalette.BandForTier(_viewModel.VimsTier);
        if (!userInitiated && !_cardController.ShouldAutoShow(band)) return;

        if (_cardWindow is null)
        {
            _cardWindow = new NotificationCardWindow();
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
                // Task 4 has no concrete "engage" action wired — spec is
                // ambiguous on what "啟動保護" means per tier. Future work
                // (Task 5 toggles or a later sprint) decides this. For now
                // the click just closes the card; user gets visual feedback
                // that the button DID something via the immediate hide.
                System.Diagnostics.Debug.WriteLine("[notification-card] Engage clicked (placeholder, no action wired yet).");
            };
            _cardWindow.MuteClicked += (_, _) =>
            {
                var currentBand = TierColorPalette.BandForTier(_viewModel.VimsTier);
                _cardController.Mute(currentBand);
                System.Diagnostics.Debug.WriteLine(
                    $"[notification-card] muted band={currentBand} until {_cardController.MutedUntilUtc(currentBand):o}");
            };
        }
        _cardWindow.SetContent(band);
        _cardWindow.Show();
        _cardWindow.Activate();
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
