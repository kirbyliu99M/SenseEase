namespace SenseEase.Core.Abstractions;

/// <summary>A single optical-flow sample emitted by a sensing provider.</summary>
public sealed class OpticalFlowSample
{
    public double OpticalFlowIndex { get; init; }
    public DateTime SampledAt { get; init; } = DateTime.Now;
}

/// <summary>
/// Sensing layer contract. MVP implementation is slider-driven (mock).
/// Future real implementations: WGC capture, gaze, ambient light.
/// </summary>
public interface ISensingProvider
{
    /// <summary>Raised whenever a new optical-flow sample is available.</summary>
    event EventHandler<OpticalFlowSample>? OpticalFlowSampled;

    void Start();
    void Stop();
}
