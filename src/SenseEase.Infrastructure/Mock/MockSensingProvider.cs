using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Mock;

/// <summary>
/// Mock sensing provider: optical flow is supplied externally (the dashboard OFI slider
/// or a demo loop) via <see cref="Push"/>. Real WGC / gaze capture is out of Phase-1 scope.
/// </summary>
public sealed class MockSensingProvider : ISensingProvider
{
    private bool _running;

    public event EventHandler<OpticalFlowSample>? OpticalFlowSampled;

    public void Start() => _running = true;

    public void Stop() => _running = false;

    /// <summary>Push one optical-flow value into the pipeline (no-op while stopped).</summary>
    public void Push(double opticalFlowIndex)
    {
        if (!_running) return;

        OpticalFlowSampled?.Invoke(this, new OpticalFlowSample
        {
            OpticalFlowIndex = opticalFlowIndex,
            SampledAt = DateTime.Now,
        });
    }
}
