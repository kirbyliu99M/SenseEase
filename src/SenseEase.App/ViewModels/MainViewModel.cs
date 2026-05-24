using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SenseEase.App.Configuration;
using SenseEase.App.Comfort;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;
using System.IO;

namespace SenseEase.App.ViewModels;

/// <summary>
/// Dashboard view model. Wires the mock Sensing ??Inference ??Feedback loop and
/// exposes bindable state for the dashboard and overlay.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    // _mockSensing:   always available for the demo slider override.
    // _cursorSensing: Phase 2.1 Win32 GetCursorPos provider.
    // _wgcSensing:    Phase 2.2 Windows.Graphics.Capture pixel-diff provider (stub until Gemini implements).
    // _sensing:       active provider, swapped at runtime by ToggleSensingMode.
    private readonly MockSensingProvider _mockSensing = new();
    private ISensingProvider _sensing;
    private readonly CursorVelocityProvider _cursorSensing = new(AppConfig.RejectCursorDragOfi);
    private readonly WgcSensingProvider _wgcSensing = new();
    private readonly IAdjustmentLog _adjustmentLog = new FileAdjustmentLog(AppConfig.AdjustmentLogPath);
    private readonly IHabituationStore _habituationStore = new FileHabituationStore(AppConfig.HabituationStatePath);
    // Auto Driver/Passenger mode detector — polls Win32 GetLastInputInfo at 1 Hz and
    // raises ModeChanged when OS-level input idleness crosses the hysteresis band.
    private readonly UserActivityModeDetector _activityDetector = new();
    private readonly IInferenceEngine _engine;
    public  IInferenceEngine Engine => _engine;
    public  ChatbotViewModel ChatbotVm { get; }
    private readonly IEventLogStore _log = new InMemoryEventLogStore();
    private readonly INluService _nlu;
    private readonly DispatcherTimer _demoTimer;
    private readonly DispatcherTimer _idleTimer; // keeps decay visible in Demo Mode when slider is idle
    private readonly DispatcherTimer _metricsTimer;
    private DateTime _lastDemoInputAtUtc = DateTime.UtcNow;
    private DateTime _lastRealSampleAt   = DateTime.UtcNow;
    private DateTime _lastMetricsTickUtc = DateTime.UtcNow;
    private bool _prevMaskActiveForIntercept;
    private readonly PressureSpikeDetector _spikeDetector = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private bool _lastMaskActive;
    private double _demoOfi;

    // Phase 4 — Lux Comfort
    public LuxComfortController? LuxController { get; private set; }

    private ISensingProvider ProviderFor(SensingMode mode) => mode switch
    {
        SensingMode.Demo => _mockSensing,
        SensingMode.Wgc  => _wgcSensing,
        _                => _cursorSensing,
    };

    public MainViewModel()
    {
        _engine = new MockInferenceEngine(_adjustmentLog, _habituationStore);
        var mistral = new MistralChatbotService(AppConfig.ModelPath, _adjustmentLog, _log);
        ChatbotVm = new ChatbotViewModel(mistral, _engine, _adjustmentLog, () => OverlayVisible, _log);
        ChatbotVm.AdjustmentApplied += (_, _) => UpdateFromState(_engine.Current);
        _nlu = new ChatbotNluService(mistral);

        _sensing = ProviderFor(AppConfig.Mode);

        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _demoTimer.Tick += OnDemoTick;

        // Fires every 300 ms to keep the LeakyIntegrator decaying when no real samples arrive.
        // Demo mode: push 0 (idle) or current slider value (active).
        // Real modes (Cursor/WGC): push OFI=0 so pressure decays if the provider stops unexpectedly.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _idleTimer.Tick += (_, _) =>
        {
            if (AppConfig.UseMockSensing && !IsRunning)
            {
                bool idle = (DateTime.UtcNow - _lastDemoInputAtUtc).TotalMilliseconds >= 300;
                _mockSensing.Push(idle ? 0.0 : _opticalFlowIndex);
            }
            else if (!AppConfig.UseMockSensing)
            {
                // If no sample has arrived in the last 600 ms the real provider has stalled;
                // step the engine with OFI=0 so pressure decays rather than freezing.
                if ((DateTime.UtcNow - _lastRealSampleAt).TotalMilliseconds > 600)
                    UpdateFromState(_engine.Update(0, Mode, DateTime.UtcNow));
            }
        };
        _idleTimer.Start();

        // ── Eye-Protection Dashboard: 1 Hz metrics ticker ──────────────────
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += (_, _) =>
        {
            DateTime now = DateTime.UtcNow;
            SessionUptime = now - _sessionStartUtc;
            if (MaskActive) SessionMaskActiveDuration += now - _lastMetricsTickUtc;
            _lastMetricsTickUtc = now;

            // Cross-component status: surface OS-chrome suppression reason from
            // WindowsActivityFilter so the Eye-Protection tab can show users when
            // OFI sensing is being intentionally paused (typing, window drag, etc.).
            try
            {
                string reason = SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.SuppressionReason;
                ActivityStatus = string.IsNullOrEmpty(reason) ? "Idle" : $"Suppressed: {reason}";
            }
            catch { /* filter unavailable — leave previous status */ }

            // Auto Driver/Passenger summary. Show live idle reading when auto-detection
            // is on, "Manual" when the user has overridden or disabled the feature.
            if (AppConfig.AutoDriverPassengerModeEnabled)
            {
                int idle = _activityDetector.LastIdleMs;
                AutoModeText = $"Auto · {Mode} · idle {idle / 1000.0:F1}s";
            }
            else
            {
                AutoModeText = "Manual";
            }
        };
        _metricsTimer.Start();

        StartDemoCommand  = new RelayCommand(_ => StartDemo(),  _ => !IsRunning);
        StartSetupDemoCommand = new RelayCommand(_ => StartSetupDemo(), _ => !IsRunning);
        SkipSetupCommand  = new RelayCommand(_ =>
        {
            AppConfig.MarkFirstRunComplete();
            ShowFirstRunBanner = false;
            AppendLog("Setup", "使用者略過首次校正 / User dismissed first-run setup.", CurrentState);
        });
        StopDemoCommand   = new RelayCommand(_ => StopDemo(),   _ => IsRunning);
        ResetStateCommand = new RelayCommand(_ => ResetState());
        ToggleOverlayCommand = new RelayCommand(_ => OverlayVisible = !OverlayVisible);
        ToggleModeCommand    = new RelayCommand(_ =>
        {
            string newMode = Mode == "Driver" ? "Passenger" : "Driver";
            Mode = newMode;
            // Manual toggle is a one-shot override, not a feature kill switch. Auto
            // detection keeps polling; it will reassert its own decision the next time
            // the idle counter crosses a threshold. Sync the detector's internal mode
            // to the user's choice so its asymmetric debounce starts measuring from
            // the manually-chosen state rather than its stale prior state.
            if (AppConfig.AutoDriverPassengerModeEnabled)
            {
                _activityDetector.OverrideMode(newMode);
                AppendLog("System",
                    $"手動切換至 {newMode}（自動偵測持續中 / auto continues）",
                    CurrentState);
            }
            else
            {
                AppendLog("System", $"手動切換至 {newMode}", CurrentState);
            }
        });
        ToggleDemoModeCommand = new RelayCommand(_ => ToggleDemoMode());
        AnalyzeFeedbackCommand = new RelayCommand(async _ => await SubmitFeedbackAsync(),
                                                  _ => !string.IsNullOrWhiteSpace(FeedbackText));
        ToggleHabituationCommand = new RelayCommand(_ =>
        {
            _engine.SetHabituationPaused(!HabituationPaused);
            UpdateFromState(_engine.Current);
            AppendLog("Habituation",
                HabituationPaused ? "再適應已暫停 (Habituation paused)" : "再適應已恢復 (Habituation resumed)",
                CurrentState);
        });

        _sensing.OpticalFlowSampled += OnOpticalFlowSampled;
        _sensing.Start();

        // Auto Driver/Passenger mode wiring. The detector polls OS-level input idleness
        // (Win32 GetLastInputInfo) at 1 Hz and pushes mode transitions through the
        // existing Mode property setter so MockInferenceEngine.Update sees the new mode
        // on the next OFI sample. The == guard avoids redundant PropertyChanged spam.
        if (AppConfig.AutoDriverPassengerModeEnabled)
        {
            _activityDetector.ModeChanged += (_, newMode) =>
                _dispatcher.Invoke(() =>
                {
                    if (Mode != newMode) Mode = newMode;
                });
            _activityDetector.Start();
        }

        CurrentState = _engine.Current;
        UpdateFromState(CurrentState);
        AppendLog("System", "SenseEase started, inference loop ready.", CurrentState);
    }

    private void ToggleDemoMode()
    {
        // Cycle is now Cursor <-> WGC only. SensingMode.Demo is no longer
        // user-cycleable: the dedicated Calibration & Demo button owns it and
        // is the only legitimate way to enter that mode at runtime. The enum
        // value stays (tests + StartSyntheticOfiRamp still reference it).
        var next = AppConfig.Mode == SensingMode.Wgc
            ? SensingMode.Cursor
            : SensingMode.Wgc;
        SetSensingMode(next, "感測模式切換");
    }

    /// <summary>Switch to the given sensing provider, unsubscribing the previous
    /// one and subscribing the new one. Idempotent: setting the current mode is
    /// a no-op. On failure, the previous provider is restored. Centralised here
    /// so both <see cref="ToggleDemoMode"/> (user-driven cycle) and the Demo
    /// video flow (which needs to force WGC and restore on exit) share the same
    /// sub/unsub logic.</summary>
    private void SetSensingMode(SensingMode target, string logPrefix)
    {
        if (AppConfig.Mode == target) return;

        ISensingProvider previous = _sensing;
        SensingMode previousMode = AppConfig.Mode;

        try
        {
            _sensing.Stop();
            _sensing.OpticalFlowSampled -= OnOpticalFlowSampled;

            AppConfig.Mode = target;
            _sensing = ProviderFor(target);

            _sensing.OpticalFlowSampled += OnOpticalFlowSampled;
            _sensing.Start();

            SensingModeLabel = ModeLabel(target);
            AppendLog("System", $"{logPrefix}: {SensingModeLabel}", CurrentState);
        }
        catch (Exception ex)
        {
            AppConfig.Mode = previousMode;
            _sensing = previous;
            _sensing.OpticalFlowSampled -= OnOpticalFlowSampled;
            _sensing.OpticalFlowSampled += OnOpticalFlowSampled;
            _sensing.Start();
            AppendLog("Error", $"感測模式切換失敗: {ex.GetType().Name}: {ex.Message}", CurrentState);
        }
    }

    private static string ModeLabel(SensingMode mode) => mode switch
    {
        SensingMode.Demo => "DEMO MODE",
        SensingMode.Wgc  => "SCREEN CAPTURE",
        _                => "CURSOR VELOCITY",
    };

    // ?? Bindable state ??????????????????????????????????????????????????????

    private string _sensingModeLabel = ModeLabel(AppConfig.Mode);
    public string SensingModeLabel
    {
        get => _sensingModeLabel;
        private set => Set(ref _sensingModeLabel, value);
    }

    // Phase 4 — Lux Comfort bindable state
    private double _currentLux;
    public double CurrentLux { get => _currentLux; private set => Set(ref _currentLux, value); }

    private int _targetContrast, _targetBrightness, _targetSharpness;
    public int TargetContrast   { get => _targetContrast;   private set => Set(ref _targetContrast,   value); }
    public int TargetBrightness { get => _targetBrightness; private set => Set(ref _targetBrightness, value); }
    public int TargetSharpness  { get => _targetSharpness;  private set => Set(ref _targetSharpness,  value); }

    private bool _luxComfortEnabled;
    public bool LuxComfortEnabled
    {
        get => _luxComfortEnabled;
        set { if (Set(ref _luxComfortEnabled, value)) LuxController?.SetEnabled(value); }
    }

    private bool _displaySupported, _alsAvailable;
    public bool DisplaySupported { get => _displaySupported; private set => Set(ref _displaySupported, value); }
    public bool AlsAvailable     { get => _alsAvailable;     private set => Set(ref _alsAvailable,     value); }

    private double _mockLux = 300.0;
    public double MockLux
    {
        get => _mockLux;
        set
        {
            if (Set(ref _mockLux, value) && AppConfig.LuxMode == LuxSensingMode.Mock)
                LuxController?.PushMockLux(value);
        }
    }

    public string LuxStatusText
    {
        get
        {
            string baseText;
            if (!AlsAvailable && AppConfig.LuxMode == LuxSensingMode.Real)
                baseText = "未偵測到環境光感測器 / No ambient light sensor detected";
            else if (!DisplaySupported)
                baseText = "此螢幕不支援 DDC/CI 控制 / This display does not support DDC/CI control";
            else
                baseText = LuxComfortEnabled
                    ? "啟用中 / Active"
                    : "已停用 / Disabled — toggle to enable";
            // Append the lux source so users can see whether ambient lux is coming
            // from ALS hardware, the RGB webcam fallback, the mock slider, etc.
            return $"{baseText} · Source: {_luxSourceLabel}";
        }
    }

    private double _opticalFlowIndex;
    public double OpticalFlowIndex
    {
        get => _opticalFlowIndex;
        set
        {
            if (Set(ref _opticalFlowIndex, value) && AppConfig.UseMockSensing)
            {
                _lastDemoInputAtUtc = DateTime.UtcNow;
                _mockSensing.Push(value); // only push from slider when in mock/demo mode
            }
        }
    }

    private double _pressure;
    public double Pressure { get => _pressure; private set => Set(ref _pressure, value); }

    private string _mode = "Passenger";
    public string Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            // Force a fresh inference tick so mode impact is immediate in overlay/state.
            if (AppConfig.UseMockSensing)
                _mockSensing.Push(_opticalFlowIndex);
        }
    }

    private int _fovRadiusPercent = SenseDefaults.OuterTransitionRadiusPercent;
    public int FovRadiusPercent { get => _fovRadiusPercent; private set => Set(ref _fovRadiusPercent, value); }

    private bool _maskActive;
    public bool MaskActive { get => _maskActive; private set => Set(ref _maskActive, value); }

    private int _fmsScore;
    public int FmsScore { get => _fmsScore; private set => Set(ref _fmsScore, value); }

    // VIMS tier (0..10). Mirrored from SenseState as a proper notifying property
    // because CurrentState is replaced wholesale by UpdateFromState and does NOT
    // raise PropertyChanged itself, so bindings like {Binding CurrentState.VimsTier}
    // would only refresh when WPF coincidentally re-evaluated them (e.g. on tab
    // switch). Bind to this property instead.
    private int _vimsTier;
    public int VimsTier { get => _vimsTier; private set => Set(ref _vimsTier, value); }

    // Human-readable label for the current lux source. Surfaces in the Display
    // Comfort card so users can see whether the lux reading is from ALS hardware,
    // the RGB webcam fallback, the mock slider, or "Not available".
    private string _luxSourceLabel = "—";
    public string LuxSourceLabel
    {
        get => _luxSourceLabel;
        private set
        {
            if (Set(ref _luxSourceLabel, value))
                OnPropertyChanged(nameof(LuxStatusText)); // status text concatenates this
        }
    }

    // Live OS-chrome suppression status from WindowsActivityFilter. Refreshed at
    // 1 Hz from the metrics timer. Empty filter reason → "Idle"; otherwise
    // "Suppressed: <reason>". Drives the small status pill in the Eye-Protection
    // tab so users can see when sensing is intentionally paused.
    private string _activityStatus = "Idle";
    public string ActivityStatus { get => _activityStatus; private set => Set(ref _activityStatus, value); }

    // Auto Driver/Passenger summary string for the metrics strip. Shows the
    // current detected mode plus the live idle reading when auto-detection is on,
    // or "Manual" when the user has opted out / overridden via the toggle button.
    private string _autoModeText = "Auto";
    public string AutoModeText { get => _autoModeText; private set => Set(ref _autoModeText, value); }

    private int _habituationLevel;
    public int HabituationLevel
    {
        get => _habituationLevel;
        private set => Set(ref _habituationLevel, value);
    }

    private bool _habituationPaused;
    public bool HabituationPaused
    {
        get => _habituationPaused;
        private set
        {
            if (Set(ref _habituationPaused, value))
                OnPropertyChanged(nameof(HabituationButtonText));
        }
    }

    public string HabituationButtonText => HabituationPaused ? "恢復再適應 / Resume" : "暫停再適應 / Pause";

    private bool _overlayVisible;
    public bool OverlayVisible
    {
        get => _overlayVisible;
        set { if (Set(ref _overlayVisible, value)) OverlayVisibilityChanged?.Invoke(this, value); }
    }

    // ── Always-on Overlay (issue #4) ─────────────────────────────────────────────
    // When enabled, the overlay window is forced visible and the VIMS tier is
    // floored to PersistentOverlayBaselineTier. Natural pressure-driven tier still
    // wins when it's higher — so a calm screen shows the baseline dim, and a
    // motion-driven spike still darkens the periphery further.
    private bool _persistentOverlayEnabled;
    public bool PersistentOverlayEnabled
    {
        get => _persistentOverlayEnabled;
        set
        {
            if (!Set(ref _persistentOverlayEnabled, value)) return;
            // Toggling on auto-opens the overlay so the feature is immediately visible.
            // Toggling off doesn't auto-hide — user keeps whatever overlay state they had.
            if (value && !OverlayVisible) OverlayVisible = true;
        }
    }

    private int _persistentOverlayBaselineTier = 4;
    /// <summary>Baseline VIMS tier (1–10) when PersistentOverlayEnabled is true. 4 is
    /// "subtle but noticeable" (~43% peak alpha at L4). Natural higher tiers override.</summary>
    public int PersistentOverlayBaselineTier
    {
        get => _persistentOverlayBaselineTier;
        set => Set(ref _persistentOverlayBaselineTier, Math.Clamp(value, 1, 10));
    }

    /// <summary>Apply the always-on tier floor to a freshly-emitted SenseState. Returns
    /// the (possibly elevated) effective tier. Pure function; safe to call from any
    /// thread.</summary>
    public int ResolveDisplayedTier(int naturalTier) =>
        PersistentOverlayEnabled
            ? Math.Max(naturalTier, PersistentOverlayBaselineTier)
            : naturalTier;

    private string _feedbackText = string.Empty;
    public string FeedbackText
    {
        get => _feedbackText;
        set
        {
            if (Set(ref _feedbackText, value))
                ((RelayCommand)AnalyzeFeedbackCommand).RaiseCanExecuteChanged();
        }
    }

    private string _nluResponse = string.Empty;
    public string NluResponse { get => _nluResponse; private set => Set(ref _nluResponse, value); }

    public Visibility NluResponseVisible =>
        string.IsNullOrEmpty(_nluResponse) ? Visibility.Collapsed : Visibility.Visible;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            ((RelayCommand)StartDemoCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopDemoCommand).RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<EventLogEntry> EventLog { get; } = new();
    public SenseState CurrentState { get; private set; }

    // ── Eye-Protection Dashboard: session metrics ─────────────────────────────
    private int _sessionFovIntercepts;
    public  int SessionFovIntercepts { get => _sessionFovIntercepts; private set => Set(ref _sessionFovIntercepts, value); }

    private double _sessionPeakOfi;
    public  double SessionPeakOfi { get => _sessionPeakOfi; private set => Set(ref _sessionPeakOfi, value); }

    private double _sessionPeakPressure;
    public  double SessionPeakPressure { get => _sessionPeakPressure; private set => Set(ref _sessionPeakPressure, value); }

    private TimeSpan _sessionMaskActiveDuration;
    public  TimeSpan SessionMaskActiveDuration { get => _sessionMaskActiveDuration; private set { if (Set(ref _sessionMaskActiveDuration, value)) OnPropertyChanged(nameof(SessionMaskActiveText)); } }
    public  string SessionMaskActiveText => FormatDuration(SessionMaskActiveDuration);

    private DateTime _sessionStartUtc = DateTime.UtcNow;
    private TimeSpan _sessionUptime;
    public  TimeSpan SessionUptime { get => _sessionUptime; private set { if (Set(ref _sessionUptime, value)) OnPropertyChanged(nameof(SessionUptimeText)); } }
    public  string SessionUptimeText => FormatDuration(SessionUptime);

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m"
            : ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}m{ts.Seconds:00}s"
                : $"{(int)ts.TotalSeconds}s";

    // Cumulative pressure integral — proxy for "visual load over the session".
    private double _cumulativePressureLoad;
    public  double CumulativePressureLoad { get => _cumulativePressureLoad; private set => Set(ref _cumulativePressureLoad, value); }

    // Ring-buffer history for the live chart (OFI, Pressure samples, both ~last 120 ticks).
    public RingHistory OfiHistory { get; } = new(ChartHistoryMax);
    public RingHistory PressureHistory { get; } = new(ChartHistoryMax);
    private const int ChartHistoryMax = 120;

    // O(1)-append fixed-size ring history. Indexed access yields oldest→newest order.
    public sealed class RingHistory : System.Collections.Generic.IReadOnlyList<double>,
                                        System.Collections.Specialized.INotifyCollectionChanged
    {
        private readonly double[] _buf;
        private int _head; // next write slot
        private int _count;
        public RingHistory(int capacity) { _buf = new double[capacity]; }
        public int Count => _count;
        public double this[int i] => _buf[(_head - _count + i + _buf.Length) % _buf.Length];
        public void Append(double v)
        {
            _buf[_head] = v;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
            CollectionChanged?.Invoke(this,
                new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                    System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
        public System.Collections.Generic.IEnumerator<double> GetEnumerator()
        {
            for (int i = 0; i < _count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public event System.Collections.Specialized.NotifyCollectionChangedEventHandler? CollectionChanged;
    }

    // ?? Events / commands ??????????????????????????????????????????????????

    public event EventHandler<bool>? OverlayVisibilityChanged;
    public event EventHandler<SenseState>? StateChanged;

    /// <summary>
    /// Raised when VIMS pressure shows a sharp relative rise (≥30% within 3s).
    /// Carries the spike magnitude 0..1. UI listens to open the chatbot popup.
    /// </summary>
    public event EventHandler<double>? PressureSpikeDetected;

    public ICommand StartDemoCommand { get; }
    public ICommand StopDemoCommand { get; }
    public ICommand ResetStateCommand { get; }
    public ICommand ToggleOverlayCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand ToggleDemoModeCommand { get; }
    public ICommand AnalyzeFeedbackCommand { get; }
    public ICommand ToggleHabituationCommand { get; }
    public ICommand StartSetupDemoCommand { get; }
    public ICommand SkipSetupCommand { get; }

    // Welcome banner state. Initialised from AppConfig.IsFirstRun so a brand-new
    // install shows the calibration prompt automatically; toggled false the
    // moment the user either completes or dismisses the setup wizard.
    private bool _showFirstRunBanner = AppConfig.IsFirstRun;
    public bool ShowFirstRunBanner
    {
        get => _showFirstRunBanner;
        set => Set(ref _showFirstRunBanner, value);
    }

    private void StartSetupDemo()
    {
        _isSetupRun = true;
        StartDemo();
        // If StartDemo bailed (no video, etc.) it didn't actually enter the
        // calibration flow — drop the flag so the next plain Start Demo press
        // doesn't accidentally trigger calibration handling.
        if (_demoVideoWindow is null) _isSetupRun = false;
    }

    // ?? Demo loop ??????????????????????????????????????????????????????????

    // Demo now plays a real first-person-view vection clip in its own window;
    // the existing WgcSensingProvider captures the primary monitor, so the
    // video's optical flow flows naturally through inference -> mask -> chatbot
    // -- i.e. the demo exercises the full real pipeline, not a synthetic OFI
    // ramp. If the video file is missing OR WGC fails to start, we fall back
    // to the legacy OFI auto-increment so a demo of *something* still runs.

    private Views.DemoVideoWindow? _demoVideoWindow;
    private SensingMode? _modeBeforeDemo;
    private bool?    _overlayBeforeDemo;
    private DateTime _demoStartedAt;
    private int      _demoMaskActivations;
    private double   _demoPeakPressure;

    // When true the next OnDemoVideoFinished pops the calibration-variant
    // summary (preset rows + Save/Skip) instead of the plain summary, and
    // a Save click writes the suggested preset to the engine + adjustment log
    // with Source="setup-demo". Set by StartSetupDemoCommand; cleared at the
    // end of OnDemoVideoFinished so a second plain "Start Demo" click after
    // calibration doesn't accidentally re-enter the calibration variant.
    private bool _isSetupRun;

    private void StartDemo()
    {
        if (IsRunning) return;

        string videoPath = AppConfig.DemoVideoPath;
        if (string.IsNullOrEmpty(videoPath))
        {
            AppendLog("Demo", "Demo video not found, running synthetic OFI ramp instead.", CurrentState);
            StartSyntheticOfiRamp();
            return;
        }

        // Reset run-scoped stats. We snapshot peak pressure / mask count over
        // the demo window so the summary log line reflects only this run.
        _demoStartedAt        = DateTime.Now;
        _demoMaskActivations  = 0;
        _demoPeakPressure     = 0;

        // Force WGC so the video's pixel motion actually drives the pipeline.
        // In Demo mode the sensor is MockSensingProvider (slider-fed) and
        // would ignore the on-screen video entirely.
        _modeBeforeDemo = AppConfig.Mode;
        SetSensingMode(SensingMode.Wgc, "Demo 啟動，切換感測");

        // Auto-enable the overlay so the mask is visible the moment pressure
        // crosses threshold — the whole point of the demo is to SHOW the
        // intervention happening on screen. Restore the prior state on close
        // (a user who had the overlay off pre-demo gets it off again after).
        _overlayBeforeDemo = OverlayVisible;
        if (!OverlayVisible) OverlayVisible = true;

        try
        {
            _demoVideoWindow = new Views.DemoVideoWindow(videoPath);
            _demoVideoWindow.DemoFinished += OnDemoVideoFinished;
            _demoVideoWindow.Owner = Application.Current?.MainWindow;
            _demoVideoWindow.Show();

            IsRunning = true;
            AppendLog("Demo", $"Demo started — playing {Path.GetFileName(videoPath)} via WGC sensing.", CurrentState);
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Demo 影片啟動失敗: {ex.GetType().Name}: {ex.Message}", CurrentState);
            if (_modeBeforeDemo is { } prior)
            {
                SetSensingMode(prior, "Demo 還原感測");
                _modeBeforeDemo = null;
            }
            StartSyntheticOfiRamp();
        }
    }

    private void StartSyntheticOfiRamp()
    {
        _demoOfi = 0;
        OpticalFlowIndex = 0;
        IsRunning = true;
        _demoTimer.Start();
        AppendLog("Demo", "OFI auto-increment running (synthetic fallback).", CurrentState);
    }

    private void StopDemo()
    {
        // Synthetic-ramp branch -- original behaviour preserved.
        if (_demoTimer.IsEnabled)
        {
            _demoTimer.Stop();
            IsRunning = false;
            AppendLog("Demo", $"Demo stopped, final OFI = {OpticalFlowIndex:F1}", CurrentState);
            return;
        }

        // Video branch -- closing the window triggers OnDemoVideoFinished,
        // which is where the real teardown / summary lives.
        if (_demoVideoWindow is { } w)
        {
            try { w.Close(); } catch { /* already closing */ }
        }
        else
        {
            IsRunning = false;
        }
    }

    private void OnDemoVideoFinished(object? sender, EventArgs e)
    {
        if (_demoVideoWindow is { } w)
        {
            w.DemoFinished -= OnDemoVideoFinished;
            _demoVideoWindow = null;
        }

        if (_modeBeforeDemo is { } prior)
        {
            SetSensingMode(prior, "Demo 結束，還原感測");
            _modeBeforeDemo = null;
        }

        // Restore overlay visibility to whatever the user had before Demo.
        if (_overlayBeforeDemo is { } priorOverlay)
        {
            if (OverlayVisible != priorOverlay) OverlayVisible = priorOverlay;
            _overlayBeforeDemo = null;
        }

        // Clear any spike state that accumulated while detection was suppressed.
        // Without this the first post-demo sample sees a fresh baseline and the
        // detector could fire a stale "spike" from the demo's peak pressure
        // decaying back to idle.
        _spikeDetector.Reset();

        TimeSpan dur = DateTime.Now - _demoStartedAt;
        AppendLog("Demo",
            $"Demo 結束 — 時長 {dur.TotalSeconds:F0}s，遮罩啟動 {_demoMaskActivations} 次，峰值壓力 {_demoPeakPressure:F0}。",
            CurrentState);

        // GTA-V-style summary modal. Opened AFTER the log line so the event-log
        // tab already reflects the demo run when the user dismisses the summary.
        // Guarded so a window-construction failure can't take down demo teardown.
        // Setup-run branch shows the calibration variant (preset rows + Save/Skip);
        // every other invocation shows the plain stats-only summary + Close.
        try
        {
            Views.DemoSummaryWindow summary;
            if (_isSetupRun)
            {
                var preset = DerivePreset(_demoPeakPressure, _demoMaskActivations);
                summary = new Views.DemoSummaryWindow(dur, _demoMaskActivations, _demoPeakPressure, preset)
                {
                    Owner = Application.Current?.MainWindow,
                };
                summary.ShowDialog(); // modal so we can read SavedPreset synchronously
                if (summary.SavedPreset)
                {
                    ApplySetupDemoPreset(preset);
                    AppendLog("Setup",
                        $"已套用建議預設值（{preset.Tier}）: FOV {preset.FovDelta:+0;-0;0}%、mask {preset.MaskDepthDelta:+0.00;-0.00;0.00}、grid {preset.GridVisibilityDelta:+0.00;-0.00;0.00}、feather {preset.FeatherWidthDelta:+0.00;-0.00;0.00}.",
                        CurrentState);
                }
                else
                {
                    AppendLog("Setup", "使用者跳過了建議預設值 / User skipped suggested preset.", CurrentState);
                }
                // Either way the user has now seen and decided on the setup
                // wizard; don't show the banner again. (Skip still counts as
                // completing first-run so the welcome prompt doesn't pester.)
                AppConfig.MarkFirstRunComplete();
                ShowFirstRunBanner = false;
            }
            else
            {
                summary = new Views.DemoSummaryWindow(dur, _demoMaskActivations, _demoPeakPressure)
                {
                    Owner = Application.Current?.MainWindow,
                };
                summary.Show();
            }
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Demo summary window failed: {ex.GetType().Name}: {ex.Message}", CurrentState);
        }

        _isSetupRun = false;
        IsRunning = false;
    }

    /// <summary>Maps observed demo reactivity (peak pressure + mask activations)
    /// to a tiered preset. Tiers: RESILIENT (low reaction), TYPICAL, SENSITIVE
    /// (high reaction). Peak pressure is primary; mask-activation count is the
    /// escalator — even with modest peak, repeated mask triggers indicate the
    /// user's threshold is being crossed often, which warrants tighter defaults.
    /// Per the user's "add on top of existing" instruction, these deltas are
    /// applied additively to the current personalization, NOT a wipe.</summary>
    private static Views.CalibrationPreset DerivePreset(double peakPressure, int maskActivations)
    {
        if (peakPressure > 85 || maskActivations >= 3)
            return new Views.CalibrationPreset("SENSITIVE", -15, 0.14, 0.07, 0.00);
        if (peakPressure > 50 || maskActivations >= 1)
            return new Views.CalibrationPreset("TYPICAL",   -8,  0.08, 0.04, 0.00);
        return new Views.CalibrationPreset("RESILIENT",     -3,  0.04, 0.02, 0.00);
    }

    /// <summary>Apply the setup-demo preset to the engine + adjustment log,
    /// using the same paths the chatbot uses for its own personalized deltas.
    /// Source="setup-demo" so the log makes the origin clear, and so future
    /// "reset everything" flows can target setup-demo entries specifically if
    /// we want to add that later.</summary>
    private void ApplySetupDemoPreset(Views.CalibrationPreset preset)
    {
        if (preset.FovDelta != 0)
        {
            int oldOffset = _engine.UserFovOffset;
            _engine.ApplyUserFeedback(preset.FovDelta);
            int newOffset = _engine.UserFovOffset;
            int applied   = newOffset - oldOffset;
            if (applied != 0)
            {
                _adjustmentLog.Append(new AdjustmentRecord
                {
                    Parameter = "FovRadius", Delta = applied,
                    OldValue  = oldOffset, NewValue = newOffset,
                    Source    = "setup-demo",
                    Utterance = $"calibration tier={preset.Tier}",
                });
            }
        }

        bool anyShader = preset.MaskDepthDelta != 0
                      || preset.GridVisibilityDelta != 0
                      || preset.FeatherWidthDelta != 0;
        if (anyShader)
        {
            var before = _engine.Current.Shader.Clone();
            _engine.ApplyShaderAdjustment(preset.MaskDepthDelta, preset.GridVisibilityDelta, preset.FeatherWidthDelta);
            var after  = _engine.Current.Shader.Clone();
            LogShaderDelta("MaskDepth",      preset.MaskDepthDelta,      before.MaskDepth,      after.MaskDepth,      preset.Tier);
            LogShaderDelta("GridVisibility", preset.GridVisibilityDelta, before.GridVisibility, after.GridVisibility, preset.Tier);
            LogShaderDelta("FeatherWidth",   preset.FeatherWidthDelta,   before.FeatherWidth,   after.FeatherWidth,   preset.Tier);
        }

        // Push the new state into the bindable surface so the dashboard
        // reflects the applied preset without waiting for the next WGC sample.
        UpdateFromState(_engine.Current);
    }

    private void LogShaderDelta(string parameter, double delta, double oldValue, double newValue, string tier)
    {
        if (delta == 0) return;
        _adjustmentLog.Append(new AdjustmentRecord
        {
            Parameter = parameter, Delta = delta,
            OldValue  = oldValue, NewValue = newValue,
            Source    = "setup-demo",
            Utterance = $"calibration tier={tier}",
        });
    }

    private void ResetState()
    {
        if (IsRunning) StopDemo();
        OpticalFlowIndex = 0;
        _engine.ResetPersonalization();
        _spikeDetector.Reset();
        UpdateFromState(_engine.Current);
        _adjustmentLog.Append(new AdjustmentRecord { Parameter = "Reset", Source = "reset" });
        NluResponse = string.Empty;
        OnPropertyChanged(nameof(NluResponseVisible));
        AppendLog("System", "State reset.", CurrentState);
    }

    private void OnDemoTick(object? sender, EventArgs e)
    {
        _demoOfi = Math.Min(_demoOfi + 3.5, 150);
        OpticalFlowIndex = _demoOfi;

        if (_demoOfi >= 150)
        {
            StopDemo();
            AppendLog("Demo", "OFI reached max 150, demo ended.", CurrentState);
        }
    }

    // ?? Inference loop ?????????????????????????????????????????????????????

    private void OnOpticalFlowSampled(object? sender, OpticalFlowSample sample)
    {
        // Sync OFI display from real provider (bypasses Push to avoid loop).
        if (!AppConfig.UseMockSensing)
        {
            _lastRealSampleAt = DateTime.UtcNow;
            _opticalFlowIndex = sample.OpticalFlowIndex;
            OnPropertyChanged(nameof(OpticalFlowIndex));
        }

        SenseState state = _engine.Update(sample.OpticalFlowIndex, Mode, sample.SampledAt);
        UpdateFromState(state);

        if (state.MaskActive != _lastMaskActive)
        {
            _lastMaskActive = state.MaskActive;
            AppendLog("Inference", state.MaskActive
                ? $"Pressure {state.Pressure:F0} exceeded threshold, FOV mask activated."
                : "Pressure returned below threshold, FOV mask released.", state);
        }
    }
    private void UpdateFromState(SenseState state)
    {
        CurrentState = state;
        Pressure = state.Pressure;
        FovRadiusPercent = state.FovRadiusPercent;
        MaskActive = state.MaskActive;
        FmsScore = state.FmsScore;
        HabituationLevel  = state.HabituationLevel;
        HabituationPaused = state.HabituationPaused;
        VimsTier          = state.VimsTier;

        // ── Eye-Protection session metrics ───────────────────────────────────────
        if (state.OpticalFlowIndex > SessionPeakOfi) SessionPeakOfi = state.OpticalFlowIndex;
        if (state.Pressure > SessionPeakPressure) SessionPeakPressure = state.Pressure;

        // ── Demo-run stats (only meaningful while a video demo is active) ──
        // The fields are also accumulated when the synthetic ramp is running,
        // which is harmless: OnDemoVideoFinished is the only consumer, and the
        // ramp branch routes through StopDemo() instead.
        if (_demoVideoWindow is not null)
        {
            if (state.Pressure > _demoPeakPressure) _demoPeakPressure = state.Pressure;
            if (state.MaskActive && !_prevMaskActiveForIntercept) _demoMaskActivations++;
        }

        // FOV intercept: count the rising edge of MaskActive.
        if (state.MaskActive && !_prevMaskActiveForIntercept)
            SessionFovIntercepts++;
        _prevMaskActiveForIntercept = state.MaskActive;

        // Cumulative pressure load (rough integral, capped to avoid runaway).
        CumulativePressureLoad = Math.Min(CumulativePressureLoad + (state.Pressure / 60.0), 1_000_000.0);

        // History ring-buffer — O(1) append via RingHistory.
        OfiHistory.Append(state.OpticalFlowIndex);
        PressureHistory.Append(state.Pressure);

        StateChanged?.Invoke(this, state);

        // Suppress spike detection (and therefore the popup AND the chatbot
        // follow-up bubble) during a demo run. The demo is intentionally a
        // vection stress test — the whole point is for the user to *experience*
        // the discomfort end-to-end before the calibration summary uses that
        // reaction to seed a preset. Interrupting them mid-clip with "Did you
        // feel a spike?" defeats the experiment AND clutters the chat thread
        // with auto-generated questions that aren't part of the user's
        // organic conversation.
        if (_demoVideoWindow is null && _spikeDetector.Push(state.Pressure, DateTime.UtcNow))
        {
            PressureSpikeDetected?.Invoke(this, _spikeDetector.LastNormalizedSpike);
        }
    }

    // ?? Feedback ???????????????????????????????????????????????????????????

    private async Task SubmitFeedbackAsync()
    {
        if (string.IsNullOrWhiteSpace(FeedbackText)) return;
        string utterance = FeedbackText.Trim();
        NluResult result = await _nlu.InterpretAsync(utterance);
        if (result.Intent == "ResetRequest")
        {
            _engine.ResetPersonalization();
            _adjustmentLog.Append(new AdjustmentRecord { Parameter = "Reset", Source = "reset", Utterance = utterance });
        }
        else
        {
            if (result.FovDelta != 0)
            {
                int oldOffset = _engine.UserFovOffset;
                _engine.ApplyUserFeedback(result.FovDelta);
                int newOffset = _engine.UserFovOffset;
                if (newOffset != oldOffset)
                {
                    _adjustmentLog.Append(new AdjustmentRecord
                    {
                        Parameter = "FovRadius", Delta = newOffset - oldOffset,
                        OldValue = oldOffset, NewValue = newOffset,
                        Source = "user", Utterance = utterance,
                    });
                }
            }
            if (result.MaskDepthDelta != 0 || result.GridVisibilityDelta != 0 || result.FeatherWidthDelta != 0)
            {
                var before = _engine.Current.Shader.Clone();
                _engine.ApplyShaderAdjustment(result.MaskDepthDelta, result.GridVisibilityDelta, result.FeatherWidthDelta);
                var after = _engine.Current.Shader.Clone();
                AppendAdjustment("MaskDepth",      result.MaskDepthDelta,      before.MaskDepth,      after.MaskDepth,      utterance);
                AppendAdjustment("GridVisibility", result.GridVisibilityDelta, before.GridVisibility, after.GridVisibility, utterance);
                AppendAdjustment("FeatherWidth",   result.FeatherWidthDelta,   before.FeatherWidth,   after.FeatherWidth,   utterance);
            }
        }
        UpdateFromState(_engine.Current);
        NluResponse = result.Response;
        OnPropertyChanged(nameof(NluResponseVisible));
        AppendLog("Feedback", $"\"{utterance}\" -> {result.Intent}", CurrentState);
    }

    private void AppendAdjustment(string parameter, double delta, double oldValue, double newValue, string utterance)
    {
        if (delta == 0) return;
        _adjustmentLog.Append(new AdjustmentRecord
        {
            Parameter = parameter, Delta = delta,
            OldValue = oldValue, NewValue = newValue,
            Source = "user", Utterance = utterance,
        });
    }

    // ?? Log ????????????????????????????????????????????????????????????????

    // Phase 4 — Lux Comfort wiring

    public void WireLuxComfort(IntPtr hwnd)
    {
        IAmbientLightProvider provider;
        LightSensorProvider.LuxSourceKind sourceKind;
        if (AppConfig.LuxMode == LuxSensingMode.Mock)
        {
            provider   = new SenseEase.Infrastructure.Mock.MockAmbientLightProvider();
            sourceKind = LightSensorProvider.LuxSourceKind.None; // labelled "Mock" below
        }
        else
        {
            // Keep the concrete type local so we can read Source — the interface
            // intentionally does not expose it (UI-only concern).
            var lsp = new LightSensorProvider(AppConfig.UseWebcamLuxFallback);
            provider   = lsp;
            sourceKind = lsp.Source;
        }
        LuxSourceLabel = sourceKind switch
        {
            LightSensorProvider.LuxSourceKind.Als    => "ALS",
            LightSensorProvider.LuxSourceKind.Webcam => "Webcam",
            _ => AppConfig.LuxMode == LuxSensingMode.Mock ? "Mock" : "Not available",
        };

        IDisplayComfortActuator actuator = hwnd == IntPtr.Zero
            ? (IDisplayComfortActuator)new NullDisplayActuator()
            : new Dxva2DisplayActuator(hwnd);

        var store = new SenseEase.Infrastructure.Storage.FileDisplayBaselineStore(AppConfig.DisplayBaselinePath);
        LuxController = new LuxComfortController(provider, actuator, store);
        App.RegisterLuxController(LuxController);
        LuxController.StateChanged += (_, _) => _dispatcher.Invoke(OnLuxControllerStateChanged);

        _ = LuxController.InitializeAsync().ContinueWith(_ =>
        {
            _dispatcher.Invoke(() =>
            {
                LuxController.Start();
                AlsAvailable = LuxController.AlsAvailable;
                DisplaySupported = LuxController.DisplaySupported;
                OnPropertyChanged(nameof(LuxStatusText));
                // If feature flag is on (default OFF), respect it; user toggles from UI.
                LuxComfortEnabled = AppConfig.AmbientLightComfortEnabled;
                AppendLog("Lux", DisplaySupported
                    ? "顯示器舒適模式已就緒 / Display comfort ready"
                    : "顯示器不支援 DDC/CI 控制 / Display does not support DDC/CI", CurrentState);
            });
        });
    }

    private void OnLuxControllerStateChanged()
    {
        if (LuxController is null) return;
        CurrentLux       = LuxController.CurrentLux;
        TargetContrast   = LuxController.TargetContrast;
        TargetBrightness = LuxController.TargetBrightness;
        TargetSharpness  = LuxController.TargetSharpness;
        DisplaySupported = LuxController.DisplaySupported;
        AlsAvailable     = LuxController.AlsAvailable;
        OnPropertyChanged(nameof(LuxStatusText));
    }

    private void AppendLog(string category, string message, SenseState? state = null)
    {
        var entry = new EventLogEntry
        {
            Category    = category,
            Message     = message,
            Ofi         = state?.OpticalFlowIndex ?? 0,
            Pressure    = state?.Pressure ?? 0,
            FovRadius   = state?.FovRadiusPercent ?? 0,
            MaskSnapshot = state?.MaskActive == true ? "ON" : "off",
        };

        _log.Append(entry);
        EventLog.Insert(0, entry);
        while (EventLog.Count > SenseDefaults.EventLogMaxCount)
            EventLog.RemoveAt(EventLog.Count - 1);
    }

    // ?? INotifyPropertyChanged ?????????????????????????????????????????????

    /// <summary>Stop auxiliary services (timers, hooks) on window close.</summary>
    public void DisposeServices()
    {
        try { _activityDetector.Dispose(); } catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
