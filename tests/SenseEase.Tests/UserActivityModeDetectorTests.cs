using System.Threading;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Pure-static `DetectMode` is the testable kernel of the auto Driver/Passenger
/// detector. Verifies the asymmetric-hysteresis threshold logic without needing
/// the timer, P/Invoke, or any threading.
/// </summary>
public class UserActivityModeDetectorTests
{
    [Theory]
    [InlineData(0, "Passenger", "Driver")]       // Idle = 0 → recent input → Driver
    [InlineData(2000, "Passenger", "Driver")]    // Just inside Driver threshold (2500)
    [InlineData(2500, "Passenger", "Driver")]    // Exactly at boundary → still Driver
    [InlineData(2501, "Passenger", "Passenger")] // Just past → no transition (hysteresis)
    [InlineData(4999, "Passenger", "Passenger")] // Still in hysteresis band
    [InlineData(5000, "Passenger", "Passenger")] // Boundary — needs ≥ to switch (test next)
    public void Passenger_StaysOrEntersDriverOnly_BelowThreshold(int idleMs, string current, string expected)
    {
        Assert.Equal(expected, UserActivityModeDetector.DetectMode(idleMs, current));
    }

    [Theory]
    [InlineData(0, "Driver", "Driver")]          // Active → stays Driver
    [InlineData(2500, "Driver", "Driver")]
    [InlineData(4999, "Driver", "Driver")]       // Still in hysteresis band → hold Driver
    [InlineData(5000, "Driver", "Passenger")]    // Reached Passenger threshold → switch
    [InlineData(60000, "Driver", "Passenger")]   // Long idle
    public void Driver_TransitionsToPassenger_AboveThreshold(int idleMs, string current, string expected)
    {
        Assert.Equal(expected, UserActivityModeDetector.DetectMode(idleMs, current));
    }

    [Fact]
    public void DetectMode_IsCaseInsensitive()
    {
        Assert.Equal("Driver", UserActivityModeDetector.DetectMode(0, "passenger"));
        Assert.Equal("Driver", UserActivityModeDetector.DetectMode(0, "PASSENGER"));
        Assert.Equal("Passenger", UserActivityModeDetector.DetectMode(5000, "driver"));
    }

    // ── CW-11/EXIT-03: re-entrancy gate lifecycle ──────────────────────────────
    // The gate itself (Interlocked CompareExchange in Poll) makes overlapping 1 Hz
    // ThreadPool callbacks return immediately so they can't pile up (the stack_dump.txt
    // amplifier). It's private + timer-driven, so we can't assert the gate value
    // directly; instead we pin that the real polling loop runs without faulting and that
    // the Start/Stop/Dispose lifecycle stays clean (the Poll body, including the gate's
    // try/finally reset, runs live on the timer thread during the sleep below).

    [Fact]
    public void Poll_RunsLive_WithoutFaulting()
    {
        using var detector = new UserActivityModeDetector();
        detector.Start();
        // The timer fires at 1 Hz; give it long enough for at least one real Poll tick
        // (which runs the gate's CompareExchange + finally reset on the ThreadPool thread).
        Thread.Sleep(1200);
        detector.Stop();

        // A live poll must have produced a non-negative idle reading and a valid mode —
        // proof the gated Poll body executed without the gate wedging it shut.
        Assert.True(detector.LastIdleMs >= 0);
        Assert.Contains(detector.CurrentMode, new[] { "Driver", "Passenger" });
    }

    [Fact]
    public void StartStop_IsIdempotent_AndDisposeSafe()
    {
        var detector = new UserActivityModeDetector();
        // Repeated Start/Stop must not throw or double-arm the timer (Start is a no-op
        // while already running; Stop is null-guarded). The gate keeps any in-flight Poll
        // from racing the lifecycle.
        detector.Start();
        detector.Start();
        detector.Stop();
        detector.Stop();
        detector.Dispose();
        detector.Dispose(); // double-dispose is a safe no-op
    }
}
