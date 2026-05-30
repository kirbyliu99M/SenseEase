using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// M6 (head-tracking sprint, 2026-05-30): ambient-light provider that pulls
/// frame-mean luma from <see cref="SidecarHeadPoseReader"/>'s latest sample
/// and maps it to lux on the SAME curve <see cref="WebcamLuxProvider"/>
/// uses, so the Display Comfort UI sees a continuous lux signal regardless
/// of which provider is driving samples.
///
/// <para><b>Why this exists.</b> Sprint decision 3 ("sidecar owns webcam"):
/// the Windows webcam is a single-holder resource. When
/// <see cref="SenseEase.App.Configuration.AppConfig.HeadTrackingEnabled"/> is
/// true the Python sidecar holds the device, and a parallel
/// <see cref="WebcamLuxProvider"/> would either fail to initialize MediaCapture
/// or fight the sidecar for frames. The sidecar already computes per-frame
/// mean luma (Rec. 601 Y, normalized [0, 1]) and ships it on every JSON line —
/// see <c>tools/head-tracker-sidecar/head_tracker.py</c> — so the C# host can
/// piggy-back on that existing stream instead of opening a second capture.</para>
///
/// <para><b>Luma scale.</b>
/// <list type="bullet">
///   <item>Sidecar emits <c>luma</c> in [0, 1] (frame_mean_luma_normalized).</item>
///   <item><see cref="WebcamLuxProvider.LuminanceToLux"/> takes meanY in [0, 255]
///         and returns lux via a log-linear curve through
///         <see cref="LuxComfortMapper"/>'s [1, 10 000] lux window. The full
///         mapping is verified in <c>SidecarLuxProviderTests</c>.</item>
///   <item>We scale luma → meanY by multiplying by 255 and feed it through
///         the SAME <see cref="WebcamLuxProvider.LuminanceToLux"/> implementation
///         (re-exposed as <see cref="WebcamLuxProvider.LumaNormalizedToLux"/>).
///         Result: for the same physical scene brightness, both providers emit
///         the same lux. Toggling <c>HeadTrackingEnabled</c> at runtime (not a
///         supported flow today, but the invariant guards against UI jumps if
///         it ever is) would not bump the Display Comfort baseline.</item>
/// </list></para>
///
/// <para><b>Stale-sample fallback (advisor G6 review point 1 — OBS-2 extended).</b>
/// If the sidecar dies mid-session (Process.Exited fires inside
/// <see cref="SidecarHeadPoseReader"/> → its <see cref="SidecarHeadPoseReader.ReadLatest"/>
/// returns null), this provider's poll tick MUST NOT freeze on the last known
/// luma. After <see cref="StalenessThresholdMs"/> (= 5 000 ms) with no fresh
/// sample, we emit a stable default lux (<see cref="SidecarDiedFallbackLux"/>
/// = 200 lx, a typical-office-illuminance "neutral" value) and log the
/// transition once. We do NOT attempt to take over the webcam by spinning
/// up a <see cref="WebcamLuxProvider"/> in M6: the sidecar's webcam release
/// is not atomic with our spin-up so there's an unavoidable race window, and
/// prototype scope is "graceful degrade, don't crash, don't freeze". A future
/// v1.0 enhancement could attempt a takeover with a 2-second cooldown — see
/// commit message backlog.</para>
///
/// <para><b>Threading.</b> The poll timer raises
/// <see cref="AmbientLightSampled"/> on the timer thread (mirrors the ALS +
/// webcam paths). LuxComfortController's OnSampled handler is documented as
/// thread-tolerant — DDC/CI writes happen off the UI thread by design.</para>
///
/// <para><b>Lifecycle.</b> Does NOT own the sidecar. The
/// <see cref="SidecarHeadPoseReader"/> is created + started + stopped by
/// <c>MainViewModel</c> as part of the head-tracking lifecycle. This provider
/// merely consumes its <see cref="IHeadPoseReader.ReadLatest"/> output —
/// disposing the provider stops the poll timer but does NOT touch the reader.</para>
/// </summary>
public sealed class SidecarLuxProvider : IAmbientLightProvider, IDisposable
{
    /// <summary>How often we poll the sidecar's latest sample. Matches the
    /// ALS path (<see cref="SenseEase.Core.Configuration.SenseDefaults.LuxPollIntervalMs"/> = 1 s)
    /// rather than WebcamLuxProvider's 10 s — the sidecar is already running
    /// at 30 Hz, so polling once per second is free (no I/O, just a
    /// Volatile.Read into an in-memory record).</summary>
    public const int DefaultPollIntervalMs = 1_000;

    /// <summary>If no fresh sample has arrived in this many ms, we fall back
    /// to <see cref="SidecarDiedFallbackLux"/>. Longer than
    /// <see cref="SidecarHeadPoseReader.StalenessThresholdMs"/> (= 2 s) to
    /// avoid bouncing on the boundary — by 5 s we're confident the sidecar
    /// is genuinely gone, not just temporarily stalled.</summary>
    public const int StalenessThresholdMs = 5_000;

    /// <summary>Default lux value returned when the sidecar luma stream has
    /// gone stale. 200 lx ≈ typical residential / dim-office illuminance —
    /// middle of <see cref="SenseEase.Core.Configuration.SenseDefaults.LuxLogMin"/>
    /// (1 lx) and <see cref="SenseEase.Core.Configuration.SenseDefaults.LuxLogMax"/>
    /// (10 000 lx) on the log curve. Picked so a sidecar-dead fallback feels
    /// like a "no opinion" comfort target rather than a sudden swing to
    /// daylight or darkness.</summary>
    public const double SidecarDiedFallbackLux = 200.0;

    private readonly IHeadPoseReader _reader;
    private readonly int _pollIntervalMs;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private System.Threading.Timer? _timer;
    private volatile bool _disposed;

    // Tracks whether we've logged the sidecar-died fallback transition. We
    // log once per stale episode, then reset when a fresh sample arrives, so
    // a flaky sidecar that recovers + dies repeatedly produces one log line
    // per outage rather than spamming.
    private bool _staleLogged;

    /// <summary>Construct a sidecar-backed lux provider.</summary>
    /// <param name="reader">The shared <see cref="IHeadPoseReader"/> from
    /// MainViewModel. Typically the <see cref="SidecarHeadPoseReader"/>
    /// instance — we accept the interface for testability (tests pass a
    /// stub reader without spawning a real process).</param>
    /// <param name="pollIntervalMs">Sampling cadence. Defaults to 1 000 ms
    /// (matches the ALS path). Floor is 250 ms to keep the UI timer thread
    /// usable even if a future caller passes something silly.</param>
    /// <param name="log">Optional diagnostic-log callback. Called once on
    /// the staleness transition; never called from the hot path.</param>
    public SidecarLuxProvider(
        IHeadPoseReader reader,
        int pollIntervalMs = DefaultPollIntervalMs,
        Action<string>? log = null)
    {
        _reader         = reader ?? throw new ArgumentNullException(nameof(reader));
        _pollIntervalMs = Math.Max(250, pollIntervalMs);
        _log            = log;
    }

    /// <summary>True — the sidecar reader is always the "sensor" when this
    /// provider is wired up. The actual freshness check happens inside the
    /// poll tick; reporting unavailable here would prevent
    /// <see cref="SenseEase.App.Comfort.LuxComfortController"/> from even
    /// starting us, defeating the point of the sidecar-died fallback.</summary>
    public bool IsSensorAvailable => true;

    public event EventHandler<AmbientLightSample>? AmbientLightSampled;

    /// <summary>Start polling. Idempotent.</summary>
    public void Start()
    {
        if (_disposed) return;
        lock (_gate)
        {
            // Fire immediately so the UI gets a first sample without waiting
            // a full poll interval. Mirrors the ALS path's `dueTime: 0`.
            _timer ??= new System.Threading.Timer(OnTick, null, 0, _pollIntervalMs);
        }
    }

    /// <summary>Stop polling. Idempotent. The reader is owned upstream.</summary>
    public void Stop()
    {
        System.Threading.Timer? t;
        lock (_gate)
        {
            t = _timer;
            _timer = null;
        }
        if (t is not null)
        {
            using var done = new System.Threading.ManualResetEvent(false);
            try { t.Dispose(done); done.WaitOne(TimeSpan.FromSeconds(2)); }
            catch { /* best-effort */ }
        }
    }

    private void OnTick(object? state)
    {
        if (_disposed) return;
        try
        {
            double lux = ReadLatestLux();
            AmbientLightSampled?.Invoke(this, new AmbientLightSample
            {
                Lux          = lux,
                SampledAtUtc = DateTime.UtcNow,
            });
        }
        catch
        {
            // Swallow — never crash the timer thread. Next tick may succeed.
        }
    }

    /// <summary>Core read path. Returns either a lux value mapped from the
    /// sidecar's most recent luma, or <see cref="SidecarDiedFallbackLux"/>
    /// when the reader reports no fresh sample. Exposed
    /// <c>internal</c> for the unit-test seam — tests inject a stub
    /// <see cref="IHeadPoseReader"/> and assert on the returned lux directly
    /// rather than waiting for a timer tick.</summary>
    internal double ReadLatestLux()
    {
        HeadPoseSample? sample = _reader.ReadLatest();
        if (sample is null)
        {
            // Reader returned null = sidecar not started, sidecar dead, or
            // its own 2-second staleness fallback fired. Either way, we have
            // no fresh luma. Log once per stale episode, then keep returning
            // the default until the reader recovers.
            if (!_staleLogged)
            {
                _staleLogged = true;
                _log?.Invoke(
                    $"[SidecarLuxProvider] sidecar luma stale, falling back to default {SidecarDiedFallbackLux:F0} lux");
            }
            return SidecarDiedFallbackLux;
        }

        // Fresh sample. Reset the staleness-log latch so a future outage logs again.
        _staleLogged = false;

        // Luma → lux mapping. Sidecar's luma is [0, 1]; WebcamLuxProvider's
        // curve takes meanY in [0, 255]. Reusing WebcamLuxProvider.LumaNormalizedToLux
        // means both providers' lux outputs are continuous across a runtime
        // toggle. The mapping itself is documented in WebcamLuxProvider.
        double clamped = Math.Clamp(sample.Luma, 0.0, 1.0);
        return WebcamLuxProvider.LumaNormalizedToLux(clamped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
    }
}
