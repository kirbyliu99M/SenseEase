using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Mock;

/// <summary>Slider-driven ambient-light provider for demo and tests.
/// <see cref="IsSensorAvailable"/> is settable so tests can exercise the no-ALS path.</summary>
public sealed class MockAmbientLightProvider : IAmbientLightProvider
{
    private bool _running;

    public bool IsSensorAvailable { get; set; } = true;

    public event EventHandler<AmbientLightSample>? AmbientLightSampled;

    public void Start() => _running = true;
    public void Stop() => _running = false;

    /// <summary>Inject a lux reading (from the dashboard slider or a test).
    /// Raised SYNCHRONOUSLY on the caller — keeps tests deterministic. In the demo
    /// (mock-mode + slider), this means the controller's Apply runs on the WPF
    /// Dispatcher; that is acceptable for the demo path. Production uses
    /// LightSensorProvider, which fires off the UI thread.</summary>
    public void Push(double lux)
    {
        if (_running)
            AmbientLightSampled?.Invoke(this, new AmbientLightSample { Lux = lux });
    }
}
