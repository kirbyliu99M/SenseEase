namespace SenseEase.Core.Abstractions;

/// <summary>A single ambient-light reading (lux) from an ALS or a mock.</summary>
public sealed class AmbientLightSample
{
    public double Lux { get; init; }
    public DateTime SampledAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Ambient-light sensing contract. Independent of ISensingProvider (OFI/VIMS) —
/// ambient light is a separate comfort axis. Real impl wraps the Windows ALS.
/// </summary>
public interface IAmbientLightProvider
{
    /// <summary>True only when real ALS hardware was found; false => degrade gracefully.</summary>
    bool IsSensorAvailable { get; }

    event EventHandler<AmbientLightSample>? AmbientLightSampled;

    void Start();
    void Stop();
}
