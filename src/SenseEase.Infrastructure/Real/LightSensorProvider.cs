using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using Windows.Devices.Sensors;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Real ambient-light provider over the Windows ALS (Windows.Devices.Sensors.LightSensor).
/// Polls on a background timer and raises AmbientLightSampled ON THE TIMER THREAD so the
/// consumer can perform slow DDC/CI writes off the UI thread.
///
/// FALLBACK BEHAVIOR (added 2026-05): on devices without an ALS, this provider can
/// optionally fall back to a <see cref="WebcamLuxProvider"/> that estimates lux from
/// the average luminance of webcam frames. The fallback is OPT-IN (constructor flag,
/// surfaced via AppConfig.UseWebcamLuxFallback) because the webcam is a privacy- and
/// power-sensitive resource. When the fallback is disabled (default), the provider
/// stays inert exactly as before when no ALS is present. The public surface
/// (<see cref="IAmbientLightProvider"/>) is unchanged regardless of which source is
/// driving samples.
/// </summary>
public sealed class LightSensorProvider : IAmbientLightProvider, IDisposable
{
    /// <summary>
    /// Which physical sensor is actually backing this provider. Surfaced so the UI
    /// can show users WHERE the lux reading is coming from — critical for the
    /// "ambient lux via RGB camera" UX, where the user should know the webcam is
    /// being sampled. Consumed by MainViewModel.LuxSourceLabel.
    /// </summary>
    public enum LuxSourceKind { None, Als, Webcam }

    /// <summary>Currently-active lux source. Set during construction; read-only afterwards.</summary>
    public LuxSourceKind Source { get; private set; } = LuxSourceKind.None;

    private readonly LightSensor? _sensor;
    private readonly WebcamLuxProvider? _webcamFallback;
    private System.Threading.Timer? _timer;

    /// <summary>Default ctor — preserves prior behavior (ALS-only, no webcam fallback).</summary>
    public LightSensorProvider() : this(useWebcamFallback: false) { }

    /// <param name="useWebcamFallback">
    /// When true AND the hardware ALS is missing, instantiate a WebcamLuxProvider and
    /// forward its samples as if they came from this provider. Default false: privacy
    /// and battery considerations make this opt-in.
    /// </param>
    public LightSensorProvider(bool useWebcamFallback)
    {
        try { _sensor = LightSensor.GetDefault(); }
        catch { _sensor = null; }

        if (_sensor is not null)
        {
            Source = LuxSourceKind.Als;
        }
        // Only stand up the webcam path if we actually need it AND the caller opted in.
        else if (useWebcamFallback)
        {
            try
            {
                var cam = new WebcamLuxProvider();
                if (cam.CanAttemptInitialization)
                {
                    cam.AmbientLightSampled += OnWebcamSample;
                    _webcamFallback = cam;
                    Source = LuxSourceKind.Webcam;
                }
                else
                {
                    cam.Dispose(); // no webcam either — stay inert
                }
            }
            catch
            {
                _webcamFallback = null; // WinRT camera APIs unavailable — stay inert
            }
        }
    }

    /// <summary>True if an ALS exists OR a webcam fallback was successfully wired up.</summary>
    public bool IsSensorAvailable => _sensor is not null || _webcamFallback is not null;

    public event EventHandler<AmbientLightSample>? AmbientLightSampled;

    public void Start()
    {
        if (_sensor is not null)
        {
            _timer ??= new System.Threading.Timer(OnTick, null, 0, SenseDefaults.LuxPollIntervalMs);
            return;
        }
        // No ALS: forward Start to the webcam fallback (if any).
        _webcamFallback?.Start();
    }

    public void Stop()
    {
        var t = _timer;
        _timer = null;
        if (t is not null)
        {
            using var done = new System.Threading.ManualResetEvent(false);
            try
            {
                t.Dispose(done);
                // Block briefly until any in-flight callback returns. Bounded to avoid
                // hanging shutdown on a stuck sensor read.
                done.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch { /* best-effort */ }
        }
        _webcamFallback?.Stop();
    }

    private void OnTick(object? state)
    {
        try
        {
            LightSensorReading? reading = _sensor?.GetCurrentReading();
            if (reading is null) return;
            AmbientLightSampled?.Invoke(this, new AmbientLightSample
            {
                Lux = reading.IlluminanceInLux,
                SampledAtUtc = DateTime.UtcNow,
            });
        }
        catch { /* transient sensor error — skip this tick */ }
    }

    private void OnWebcamSample(object? sender, AmbientLightSample sample)
    {
        // Re-raise on the webcam timer thread. Same threading contract as ALS path:
        // consumer (LuxComfortController) runs DDC/CI off the UI thread.
        AmbientLightSampled?.Invoke(this, sample);
    }

    public void Dispose()
    {
        Stop();
        _timer = null;
        if (_webcamFallback is not null)
        {
            try { _webcamFallback.AmbientLightSampled -= OnWebcamSample; } catch { }
            try { _webcamFallback.Dispose(); } catch { }
        }
    }
}
