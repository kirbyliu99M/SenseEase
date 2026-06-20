using System;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Idle-unload (ultrabook plan #1, 2026-06-21): the ~10-minute inactivity window after which the
/// llama-server is killed to free its ~3 GB; the next message re-loads on demand. The actual process
/// kill needs a real server (covered by the manual integration check in the plan); these pin the TIMING
/// decision deterministically via the injectable clock + timeout — no real server, no real-time wait.
/// </summary>
public class LocalLlmChatbotServiceIdleUnloadTests
{
    // A path that does not exist → IsModelAvailable is false, so no real llama-server is ever spawned.
    private const string NoModel = "C:/nonexistent/senseease-test-model.gguf";

    private static LocalLlmChatbotService Make(Func<DateTime> clock, TimeSpan idle)
        => new(NoModel, utcNow: clock, idleTimeout: idle);

    [Fact]
    public void IdleWindow_NotElapsed_WhileWithinTimeout()
    {
        var t0 = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = t0;
        using var svc = Make(() => now, TimeSpan.FromMinutes(10));
        svc.MarkActivityNowForTest();

        Assert.False(svc.IdleWindowElapsedForTest());                 // stamped "just now"
        now = t0.AddMinutes(9).AddSeconds(59);
        Assert.False(svc.IdleWindowElapsedForTest());                 // 9:59 — still warm
    }

    [Fact]
    public void IdleWindow_Elapsed_OncePastTimeout()
    {
        var t0 = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = t0;
        using var svc = Make(() => now, TimeSpan.FromMinutes(10));
        svc.MarkActivityNowForTest();

        now = t0.AddMinutes(10).AddSeconds(1);
        Assert.True(svc.IdleWindowElapsedForTest());                  // past 10 min → eligible to unload
    }

    [Fact]
    public void ZeroTimeout_DisablesUnload_ModelStaysResident()
    {
        var t0 = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = t0;
        using var svc = Make(() => now, TimeSpan.Zero);               // <= 0 disables idle-unload
        svc.MarkActivityNowForTest();

        now = t0.AddHours(5);
        Assert.False(svc.IdleWindowElapsedForTest());                 // never elapses when disabled
    }

    [Fact]
    public void MaybeUnloadIfIdle_NoModelLoaded_IsSafeNoOp_AndDisposeOk()
    {
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var svc = Make(() => now, TimeSpan.FromMinutes(10));

        // Nothing is loaded (IsModelAvailable=false) → the poll must be an inert no-op, never throw,
        // and never flip IsModelLoaded.
        svc.MaybeUnloadIfIdle();
        Assert.False(svc.IsModelLoaded);

        svc.Dispose();   // exercises the timer-dispose path (timer never armed here) — must not throw
    }
}
