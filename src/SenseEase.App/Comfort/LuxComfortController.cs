using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;

namespace SenseEase.App.Comfort;

/// <summary>
/// Coordinates the ambient-light → display-comfort pipeline. Subscribes to an
/// IAmbientLightProvider, ticks a LuxComfortMapper, and applies the smoothed target
/// to an IDisplayComfortActuator with hysteresis. Owns the full lifecycle including
/// restore-on-exit. All DDC/CI writes run on the provider's TIMER THREAD (not the
/// WPF Dispatcher) so the UI never blocks on a slow monitor.
/// </summary>
public sealed class LuxComfortController : IDisposable
{
    private readonly IAmbientLightProvider _provider;
    private readonly IDisplayComfortActuator _actuator;
    private readonly IDisplayBaselineStore _store;
    private readonly LuxComfortMapper _mapper = new();
    private readonly object _gate = new();

    private int _lastAppliedContrast = -1;
    private int _lastAppliedBrightness = -1;
    private int _lastAppliedSharpness = -1;

    private bool _enabled;
    private bool _initialized;
    private bool _started;
    private bool _shutDown;
    private string _monitorSignature = string.Empty;

    public LuxComfortController(IAmbientLightProvider provider,
                                IDisplayComfortActuator actuator,
                                IDisplayBaselineStore store)
    {
        _provider = provider;
        _actuator = actuator;
        _store = store;
        _provider.AmbientLightSampled += OnSampled;
    }

    /// <summary>Raised after every sample (state for UI binding has new values).</summary>
    public event EventHandler? StateChanged;

    public double CurrentLux       { get; private set; }
    public double SmoothedLux      => _mapper.SmoothedLux;
    public int    TargetContrast   { get; private set; }
    public int    TargetBrightness { get; private set; }
    public int    TargetSharpness  { get; private set; }
    public DisplayCapability Capability => _actuator.Capability;
    public bool AlsAvailable        => _provider.IsSensorAvailable;
    public bool DisplaySupported    => _actuator.Capability.AnySupported;
    public bool IsEnabled           => _enabled;
    public bool IsInitialized       => _initialized;

    /// <summary>Probe the display, capture &amp; persist the baseline. Async so a slow
    /// DDC/CI probe doesn't block app startup. Safe to call once after the HWND exists.</summary>
    public Task InitializeAsync() => Task.Run(() =>
    {
        try { InitializeCore(); } catch { /* never crash the app */ }
    });

    private void InitializeCore()
    {
        lock (_gate)
        {
            if (_initialized || _shutDown) return;

            // Recovery candidate: a prior session that did not cleanly restore.
            DisplayBaselineRecord? prior = null;
            try { prior = _store.Load(); } catch { }
            DisplayBaseline? recover = null;
            if (prior is not null
                && !prior.CleanShutdown
                && (DateTime.UtcNow - prior.CapturedAtUtc).TotalHours <= SenseDefaults.BaselineRecoveryMaxAgeHours)
            {
                recover = prior.Baseline; // monitor-signature match is re-validated after probe
            }

            // Probe (optionally restoring `recover` first so captured originals are the true originals).
            _actuator.Initialize(recover);
            _monitorSignature = _actuator.MonitorSignature;

            // If we did a recovery write but the monitor signature does not match the
            // persisted one, discard the recovery (we wrote the wrong values onto a
            // different monitor — best we can do is leave probed values and warn).
            if (recover is not null && prior is not null
                && !string.IsNullOrEmpty(prior.MonitorSignature)
                && prior.MonitorSignature != _monitorSignature)
            {
                // Different monitor — clear the stale record so future launches don't recover onto it.
                try { _store.Clear(); } catch { }
            }

            // CRITICAL ORDERING: persist the freshly captured baseline BEFORE any Apply()
            // can run, so a force-kill before the first restore still leaves a record.
            if (_actuator.IsInitialized)
            {
                try
                {
                    _store.Save(new DisplayBaselineRecord
                    {
                        Baseline = _actuator.Baseline,
                        MonitorSignature = _monitorSignature,
                        CapturedAtUtc = DateTime.UtcNow,
                        CleanShutdown = false,
                    });
                }
                catch { }
            }

            _initialized = true;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enable/disable adjustment. Disabling immediately restores the baseline.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (!enabled)
            {
                _actuator.RestoreBaseline();
                _lastAppliedContrast = _lastAppliedBrightness = _lastAppliedSharpness = -1;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Start the underlying provider (idempotent).</summary>
    public void Start()
    {
        if (_shutDown || _started) return;
        _started = true;
        _provider.Start();
    }

    /// <summary>Inject a lux value when in mock mode (slider).</summary>
    public void PushMockLux(double lux)
    {
        if (_provider is Infrastructure.Mock.MockAmbientLightProvider mock)
            mock.Push(lux);
    }

    private void OnSampled(object? sender, AmbientLightSample sample)
    {
        // Runs on the provider's timer thread — DDC/CI writes are safe here.
        bool shouldApply;
        int c, b, s;
        lock (_gate)
        {
            if (_shutDown) return;

            CurrentLux = sample.Lux;
            var target = _mapper.Update(sample.Lux);
            TargetContrast   = target.ContrastPercent;
            TargetBrightness = target.BrightnessPercent;
            TargetSharpness  = target.SharpnessPercent;

            c = target.ContrastPercent;
            b = target.BrightnessPercent;
            s = target.SharpnessPercent;

            shouldApply = _enabled && _initialized && _actuator.IsInitialized
                && (PastDeadband(c, _lastAppliedContrast)
                 || PastDeadband(b, _lastAppliedBrightness)
                 || PastDeadband(s, _lastAppliedSharpness));

            if (shouldApply)
            {
                _lastAppliedContrast   = c;
                _lastAppliedBrightness = b;
                _lastAppliedSharpness  = s;
            }
        }

        if (shouldApply)
        {
            try { _actuator.Apply(c, b, s); }
            catch { /* DDC/CI write failed; will retry next tick */ }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool PastDeadband(int newVal, int lastApplied)
        => lastApplied < 0
        || Math.Abs(newVal - lastApplied) >= SenseDefaults.LuxHysteresisDeadbandPercent;

    /// <summary>Restore the baseline, stop the provider, mark the persisted record clean.
    /// Idempotent — safe to call from OnExit, MainWindow.Closed, crash handlers.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutDown) { /* still mark clean once more is safe; fall through */ }
            _shutDown = true;
            _enabled = false;
            _started = false;
            try { _provider.Stop(); } catch { }
            try { _actuator.RestoreBaseline(); } catch { }
            try
            {
                var rec = _store.Load();
                if (rec is not null)
                {
                    rec.CleanShutdown = true;
                    _store.Save(rec);
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        try { Shutdown(); } catch { }
        try { _actuator.Dispose(); } catch { }
        if (_provider is IDisposable d) { try { d.Dispose(); } catch { } }
    }
}
