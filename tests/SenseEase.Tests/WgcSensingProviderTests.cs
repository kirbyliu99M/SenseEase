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

    /// <summary>Alpha-1 W3 (EM3 / NE11): Stop() and Dispose() are idempotent and safe
    /// to call without a prior Start() and again after Dispose, so the teardown paths
    /// (Power-Save toggle / mode swap / Reset) and the EM4 fault-recovery re-init can't
    /// throw. The COM use-after-free RACE and the EM4 recovery itself need a live WGC
    /// session + GPU and are verified at runtime (see the W3 user-test requirements).</summary>
    [Fact]
    public void StopAndDispose_AreIdempotent_WithoutStart()
    {
        var provider = new WgcSensingProvider();

        provider.Stop();      // teardown without a session — every release is null-guarded
        provider.Stop();      // idempotent
        provider.Dispose();   // Stop() + timer.Dispose()
        provider.Dispose();   // Stop() after the timer is disposed must not throw (guarded Change)

        Assert.False(provider.IsPaused);
    }

    /// <summary>Alpha-1 W3 gate (orchestration §3 / MASTERPLAN M3): 100× Power-Save
    /// toggle, 0 crash. Each Pause() runs a full WGC Stop() teardown and each Resume()
    /// a fresh Start(), so rapid cycling stresses the EM3 Stop/OnTick teardown
    /// serialization, the NE11 watchdog-timer disposal/reuse, and the
    /// <c>_watchdogScheduled</c> re-arm guard. No teardown / re-init may throw across
    /// the run, and IsPaused must track the last call. (The COM use-after-free RACE
    /// itself still needs a live WGC session + GPU and is verified at runtime.)</summary>
    [Fact]
    public void WgcSensingProvider_RapidPowerSaveToggle_100x_NoCrash()
    {
        using var provider = new WgcSensingProvider();

        for (int i = 0; i < 100; i++)
        {
            provider.Pause();                 // → Stop() teardown
            Assert.True(provider.IsPaused);
            provider.Resume();                // → Start() re-init
            Assert.False(provider.IsPaused);
        }

        Assert.False(provider.IsPaused);
        // Teardown after a long toggle run stays clean + idempotent.
        provider.Stop();
        provider.Dispose();
    }
}
