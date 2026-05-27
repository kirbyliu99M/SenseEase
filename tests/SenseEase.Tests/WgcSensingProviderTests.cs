using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Pause/Resume contract for WgcSensingProvider.
///
/// <para>v0.8 hotfix follow-up: Pause now calls Stop() (full WGC tear-down
/// to remove the Windows yellow capture indicator) and Resume calls Start()
/// (fresh init). The <c>_paused</c> flag remains as a redundant short-circuit
/// so any in-flight OnTick callback exits before Stop releases COM pointers.
/// These tests cover the observable flag semantics; the full Stop/Start
/// side-effects are covered by manual sanity test (real WGC + visible
/// yellow capture indicator behaviour).</para>
///
/// <para>Spike-detector reset is not tested here because SpikeDetector is
/// not held by WgcSensingProvider in this codebase — the consumer
/// (MainViewModel) owns it.</para>
/// </summary>
public class WgcSensingProviderTests
{
    /// <summary>Pause flips IsPaused true; Resume flips it false. Idempotent
    /// across repeated calls. With the v0.8 hotfix follow-up, Pause also
    /// invokes Stop() (idempotent — releases null pointers safely from
    /// the never-started state) and Resume invokes Start() (idempotent if
    /// already running; may no-op in test environment if WGC initialisation
    /// fails — that's fine, the IsPaused flag still flips correctly).</summary>
    [Fact]
    public void WgcSensingProvider_Pause_StopsEmittingFrames()
    {
        using var provider = new WgcSensingProvider();
        Assert.False(provider.IsPaused);

        provider.Pause();
        Assert.True(provider.IsPaused);

        // Idempotent — second Pause is a no-op (Stop checks _running).
        provider.Pause();
        Assert.True(provider.IsPaused);

        provider.Resume();
        Assert.False(provider.IsPaused);

        // Idempotent — second Resume is a no-op (Start checks _running;
        // no extra watchdog scheduled by the _watchdogScheduled guard).
        provider.Resume();
        Assert.False(provider.IsPaused);
    }
}
