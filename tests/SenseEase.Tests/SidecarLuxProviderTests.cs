using System;
using System.Collections.Generic;
using System.Threading;
using SenseEase.Core.Abstractions;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>M6 (head-tracking sprint) gate tests for
/// <see cref="SidecarLuxProvider"/>. Pins the three contracts advisor G6
/// flagged for review:
/// <list type="bullet">
///   <item>Luma scale consistency: SidecarLuxProvider's [0, 1] normalized
///         luma maps through WebcamLuxProvider's calibrated [0, 255] curve,
///         so both providers emit the same lux for the same scene
///         brightness — no Display Comfort UI jump on a runtime toggle.</item>
///   <item>Sidecar-death fallback (OBS-2 extended): when the reader reports
///         no fresh sample (null), ReadLatestLux returns a stable default
///         (~200 lx) instead of freezing on the last known luma.</item>
///   <item>Null-sample path: head-tracking off / sidecar not yet started /
///         sidecar dead — all collapse to the same default-lux behavior.</item>
/// </list>
/// Tests inject a synthetic <see cref="IHeadPoseReader"/> stub so they exercise
/// the lux-mapping + staleness logic without spawning a real sidecar process
/// — mirrors the SidecarHeadPoseReaderTests TestHook pattern.</summary>
public class SidecarLuxProviderTests
{
    // ── 1) Luma → lux mapping uses the shared WebcamLuxProvider curve ───────

    [Fact]
    public void SidecarLuxProvider_FreshSample_ReturnsLuxFromLuma()
    {
        // Mid-range luma should map to roughly mid-range lux (~200 lx per
        // the WebcamLuxProvider calibration table: meanY=128 → ~200 lx).
        // 0.5 normalized = 127.5 on the [0, 255] scale, so we expect lux
        // close to but slightly under 200.
        var reader = new StubHeadPoseReader();
        reader.SetSample(new HeadPoseSample(
            TimestampMs: 1, Cx: 0.5, Cy: 0.5,
            Luma: 0.5,
            FaceDetected: true, FacePresent: 1.0));

        using var provider = new SidecarLuxProvider(reader);

        double lux = provider.ReadLatestLux();

        // The exact value comes from the calibration curve; just assert
        // it's in the mid-range band and matches WebcamLuxProvider exactly.
        double expected = WebcamLuxProvider.LumaNormalizedToLux(0.5);
        Assert.Equal(expected, lux, 6);
        Assert.InRange(lux, 100.0, 300.0); // sanity band around "typical indoor"
    }

    [Fact]
    public void SidecarLuxProvider_DarkLuma_MapsToLowLux()
    {
        // Very-dark scene (luma near 0) should produce a low lux value
        // anchored at the WebcamLuxProvider curve floor (~5 lx for meanY=0).
        var reader = new StubHeadPoseReader();
        reader.SetSample(new HeadPoseSample(
            TimestampMs: 1, Cx: 0.5, Cy: 0.5,
            Luma: 0.0,
            FaceDetected: true, FacePresent: 1.0));

        using var provider = new SidecarLuxProvider(reader);

        double lux = provider.ReadLatestLux();
        Assert.InRange(lux, 4.0, 10.0); // dark-room band
        Assert.Equal(WebcamLuxProvider.LumaNormalizedToLux(0.0), lux, 6);
    }

    [Fact]
    public void SidecarLuxProvider_BrightLuma_MapsToHighLux()
    {
        // Near-saturation luma should land in the "very bright" band
        // (~5 000 lx per the curve's top end at meanY=255).
        var reader = new StubHeadPoseReader();
        reader.SetSample(new HeadPoseSample(
            TimestampMs: 1, Cx: 0.5, Cy: 0.5,
            Luma: 1.0,
            FaceDetected: true, FacePresent: 1.0));

        using var provider = new SidecarLuxProvider(reader);

        double lux = provider.ReadLatestLux();
        Assert.InRange(lux, 4000.0, 6000.0); // bright daylight band
        Assert.Equal(WebcamLuxProvider.LumaNormalizedToLux(1.0), lux, 6);
    }

    [Fact]
    public void SidecarLuxProvider_OutOfRangeLuma_IsClamped()
    {
        // Defensive: a buggy future sidecar emitting luma > 1 or < 0 must
        // not crash or produce NaN/Infinity. The provider clamps before
        // mapping, so out-of-range input collapses to the curve endpoints.
        var reader = new StubHeadPoseReader();
        reader.SetSample(new HeadPoseSample(
            TimestampMs: 1, Cx: 0.5, Cy: 0.5,
            Luma: 1.5, // out of range
            FaceDetected: true, FacePresent: 1.0));

        using var provider = new SidecarLuxProvider(reader);

        double luxHigh = provider.ReadLatestLux();
        Assert.Equal(WebcamLuxProvider.LumaNormalizedToLux(1.0), luxHigh, 6);

        reader.SetSample(new HeadPoseSample(
            TimestampMs: 1, Cx: 0.5, Cy: 0.5,
            Luma: -0.2, // out of range
            FaceDetected: true, FacePresent: 1.0));
        double luxLow = provider.ReadLatestLux();
        Assert.Equal(WebcamLuxProvider.LumaNormalizedToLux(0.0), luxLow, 6);
    }

    // ── 2) Luma scale verification: SidecarLuxProvider matches WebcamLuxProvider ─

    [Theory]
    [InlineData(0.0,    0.0)]    // dark room
    [InlineData(0.235,  60.0)]   // 60/255 ≈ 0.235 — dim corridor
    [InlineData(0.502,  128.0)]  // 128/255 ≈ 0.502 — typical office
    [InlineData(0.745,  190.0)]  // 190/255 ≈ 0.745 — well-lit office
    [InlineData(0.902,  230.0)]  // 230/255 ≈ 0.902 — very bright indoor
    [InlineData(1.0,    255.0)]  // full saturation
    public void SidecarLuxProvider_LumaScale_MatchesWebcamLuxProviderCurve(
        double sidecarLumaNormalized, double webcamMeanY255)
    {
        // The load-bearing G6 invariant: at any equivalent scene brightness
        // (same Y value on the two providers' input scales), SidecarLuxProvider
        // and WebcamLuxProvider produce the SAME lux output. This guarantees
        // the Display Comfort UI's perceived ambient baseline doesn't jump
        // when toggling HeadTrackingEnabled. Both providers share the
        // LuminanceToLux helper inside WebcamLuxProvider — we just verify the
        // [0,1] → [0,255] scaling bridge is correct.
        double sidecarLux = WebcamLuxProvider.LumaNormalizedToLux(sidecarLumaNormalized);
        double webcamLux  = WebcamLuxProvider.LuminanceToLux(webcamMeanY255);

        // Tolerance accounts for the discretization of the [0, 1] / [0, 255]
        // scales (0.235 * 255 ≈ 59.925, not exactly 60). The curve is
        // continuous so a sub-1-unit input difference produces a tiny output
        // difference well under the LuxComfortMapper's hysteresis deadband.
        double relativeDiff = Math.Abs(sidecarLux - webcamLux) / Math.Max(webcamLux, 1.0);
        Assert.True(relativeDiff < 0.02,
            $"Lux mismatch at luma={sidecarLumaNormalized}/meanY={webcamMeanY255}: " +
            $"sidecar={sidecarLux:F2}, webcam={webcamLux:F2}, relDiff={relativeDiff:P2}");
    }

    // ── 3) Stale-sample fallback (OBS-2 extended) ───────────────────────────

    [Fact]
    public void SidecarLuxProvider_StaleSample_FallsBackToDefault()
    {
        // Advisor G6 review point 1: when the sidecar dies mid-session,
        // the reader's ReadLatest() returns null (sidecar's own staleness or
        // process-exit fallback). SidecarLuxProvider must NOT freeze on the
        // last good luma — it returns the documented fallback lux instead.
        var reader = new StubHeadPoseReader();
        reader.SetSample(null); // simulate sidecar dead / stale

        using var provider = new SidecarLuxProvider(reader);

        double lux = provider.ReadLatestLux();
        Assert.Equal(SidecarLuxProvider.SidecarDiedFallbackLux, lux, 6);
    }

    [Fact]
    public void SidecarLuxProvider_StaleSample_LogsTransitionOnce()
    {
        // Log discipline: one log line per stale episode, not one per tick.
        // A noisy log would mask other diagnostics in the event-log surface.
        var reader = new StubHeadPoseReader();
        reader.SetSample(null);
        var logged = new List<string>();
        using var provider = new SidecarLuxProvider(reader, log: msg => logged.Add(msg));

        // Three reads while stale → one log line.
        provider.ReadLatestLux();
        provider.ReadLatestLux();
        provider.ReadLatestLux();
        Assert.Single(logged);
        Assert.Contains("stale", logged[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("falling back", logged[0], StringComparison.OrdinalIgnoreCase);

        // Fresh sample arrives → stale latch resets → next stale episode
        // produces a fresh log line.
        reader.SetSample(new HeadPoseSample(1, 0.5, 0.5, 0.5, true, 1.0));
        provider.ReadLatestLux();
        Assert.Single(logged); // still 1, fresh sample doesn't log

        reader.SetSample(null);
        provider.ReadLatestLux();
        Assert.Equal(2, logged.Count); // new stale episode → new log
    }

    [Fact]
    public void SidecarLuxProvider_FreshSampleAfterStale_RecoversCleanly()
    {
        // Recovery contract: after the sidecar comes back from a stale
        // episode, ReadLatestLux must return the fresh-mapped value (not
        // the cached fallback). Belt-and-braces against any latch-bug
        // that would freeze the provider on the default lux.
        var reader = new StubHeadPoseReader();

        using var provider = new SidecarLuxProvider(reader);

        reader.SetSample(null);
        Assert.Equal(SidecarLuxProvider.SidecarDiedFallbackLux,
                     provider.ReadLatestLux(), 6);

        reader.SetSample(new HeadPoseSample(1, 0.5, 0.5, 0.5, true, 1.0));
        double fresh = provider.ReadLatestLux();
        Assert.NotEqual(SidecarLuxProvider.SidecarDiedFallbackLux, fresh);
        Assert.Equal(WebcamLuxProvider.LumaNormalizedToLux(0.5), fresh, 6);
    }

    // ── 4) Null-sample / head-tracking-off path ────────────────────────────

    [Fact]
    public void SidecarLuxProvider_NullSample_ReturnsDefault()
    {
        // The null-sample case covers three production scenarios:
        //  • Head-tracking enabled but sidecar hasn't started yet (race
        //    between WireLuxComfort and StartHeadTrackingIfEnabled).
        //  • Sidecar died (Process.Exited fired inside the reader).
        //  • Sidecar wedged for >2 s (reader's own staleness threshold).
        // All three collapse to "ReadLatest() returns null" → same fallback.
        var reader = new StubHeadPoseReader(); // never given a sample

        using var provider = new SidecarLuxProvider(reader);

        double lux = provider.ReadLatestLux();
        Assert.Equal(SidecarLuxProvider.SidecarDiedFallbackLux, lux, 6);
    }

    [Fact]
    public void SidecarLuxProvider_DefaultLux_IsInValidRange()
    {
        // The fallback default lives in LuxComfortMapper's log10 window
        // [LuxLogMin=0 (1 lx), LuxLogMax=4 (10 000 lx)]. A default outside
        // that range would clip and produce nonsense actuator targets.
        Assert.InRange(SidecarLuxProvider.SidecarDiedFallbackLux, 1.0, 10_000.0);
    }

    // ── 5) Lifecycle: ctor null-check + start/stop idempotency ─────────────

    [Fact]
    public void SidecarLuxProvider_NullReader_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SidecarLuxProvider(reader: null!));
    }

    [Fact]
    public void SidecarLuxProvider_StartStop_AreIdempotent()
    {
        var reader = new StubHeadPoseReader();
        using var provider = new SidecarLuxProvider(reader);

        // No exceptions on repeated lifecycle calls.
        provider.Start();
        provider.Start();
        provider.Stop();
        provider.Stop();
        provider.Start();
        provider.Stop();
    }

    [Fact]
    public void SidecarLuxProvider_IsSensorAvailable_AlwaysTrue()
    {
        // Reporting unavailable would prevent LuxComfortController from
        // calling Start(), which would defeat the sidecar-died fallback —
        // we WANT to be polled so we can emit the default lux when the
        // sidecar is unhealthy. The freshness check lives in the read path.
        var reader = new StubHeadPoseReader();
        using var provider = new SidecarLuxProvider(reader);
        Assert.True(provider.IsSensorAvailable);
    }

    [Fact]
    public void SidecarLuxProvider_RaisesAmbientLightSampled_OnPoll()
    {
        // Timer-driven event flow: subscribers receive AmbientLightSample
        // events on the timer thread. Verify by polling at 100 ms cadence
        // and confirming at least one event arrives within 500 ms.
        var reader = new StubHeadPoseReader();
        reader.SetSample(new HeadPoseSample(1, 0.5, 0.5, 0.5, true, 1.0));

        using var provider = new SidecarLuxProvider(reader, pollIntervalMs: 250);

        var received = new List<AmbientLightSample>();
        var gate = new ManualResetEventSlim(false);
        provider.AmbientLightSampled += (_, s) =>
        {
            lock (received)
            {
                received.Add(s);
                if (received.Count >= 1) gate.Set();
            }
        };
        provider.Start();
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
        provider.Stop();

        Assert.NotEmpty(received);
        // The expected lux for luma=0.5 should match the shared curve.
        Assert.Equal(WebcamLuxProvider.LumaNormalizedToLux(0.5), received[0].Lux, 6);
    }

    // ── Test stub ───────────────────────────────────────────────────────────

    /// <summary>Simple <see cref="IHeadPoseReader"/> stub that returns
    /// whatever sample the test most recently set. Synchronized so the
    /// timer-thread test path is safe.</summary>
    private sealed class StubHeadPoseReader : IHeadPoseReader
    {
        private HeadPoseSample? _sample;
        public void SetSample(HeadPoseSample? s) => Volatile.Write(ref _sample, s);
        public HeadPoseSample? ReadLatest() => Volatile.Read(ref _sample);
    }
}
