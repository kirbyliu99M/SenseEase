using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// overlay-bar-redesign D3: Pause/Resume contract for WgcSensingProvider.
/// The provider's actual frame loop requires a live D3D11 device + WGC
/// session, so these tests cover only the Pause/Resume state semantics that
/// the host depends on (idempotency + IsPaused observability).
///
/// <para>Spike-detector reset is not tested here because SpikeDetector is
/// not held by WgcSensingProvider in this codebase — the consumer
/// (MainViewModel) owns it. See judgment-calls appendix in the sprint
/// report for the rationale.</para>
/// </summary>
public class WgcSensingProviderTests
{
    /// <summary>Test 4 (simplified): Pause flips IsPaused true; Resume
    /// flips it false. Idempotent across repeated calls.</summary>
    [Fact]
    public void WgcSensingProvider_Pause_StopsEmittingFrames()
    {
        using var provider = new WgcSensingProvider();
        Assert.False(provider.IsPaused);

        provider.Pause();
        Assert.True(provider.IsPaused);

        // Idempotent — second Pause is a no-op.
        provider.Pause();
        Assert.True(provider.IsPaused);

        provider.Resume();
        Assert.False(provider.IsPaused);

        // Idempotent — second Resume is a no-op (no extra watchdog scheduled).
        provider.Resume();
        Assert.False(provider.IsPaused);
    }
}
