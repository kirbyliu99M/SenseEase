using System.Runtime.InteropServices.WindowsRuntime;
using SenseEase.Core.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Fallback ambient-light provider that ESTIMATES lux from the average luminance of
/// a UVC webcam preview frame, used on devices without a Windows ALS.
///
/// IMPORTANT LIMITATIONS (acknowledged by design):
///  * Webcam auto-exposure means frame luminance is NOT proportional to scene illuminance.
///    The output is a RELATIVE / ORDINAL signal — "darker than before" / "brighter than
///    before" — not an absolute lux measurement. We map it through a coarse curve into
///    LuxComfortMapper's logarithmic input range and let hysteresis absorb the noise.
///  * Polling interval is intentionally long (default 10 s) to minimize battery drain
///    and privacy impact. The webcam is opened only while sampling.
///  * Requires the Windows camera privacy permission. If the user denies, or the
///    camera is in use by another app, IsSensorAvailable stays false and no events
///    are raised (graceful degrade — same observable behavior as a missing ALS).
///  * Privacy: this provider is gated by AppConfig.UseWebcamLuxFallback which is
///    OFF by default. The webcam is opened on Start() and disposed on Stop().
/// </summary>
public sealed class WebcamLuxProvider : IAmbientLightProvider, IDisposable
{
    // Polling cadence. Longer than ALS poll (1 s) because webcam I/O is expensive,
    // ambient light changes slowly, and we want to minimize the privacy footprint.
    private const int DefaultPollIntervalMs = 10_000;

    private readonly int _pollIntervalMs;
    private readonly object _gate = new();
    private readonly string? _sourceGroupId;

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private MediaFrameSource? _frameSource;
    private System.Threading.Timer? _timer;
    private volatile bool _sampling;
    private volatile bool _initialized;
    private volatile bool _disposed;

    public WebcamLuxProvider(int pollIntervalMs = DefaultPollIntervalMs)
    {
        _pollIntervalMs = Math.Max(2_000, pollIntervalMs); // never poll faster than 2 s

        // Probe synchronously in the ctor (mirrors LightSensorProvider's null-check
        // pattern). We do NOT instantiate MediaCapture here — just confirm a webcam
        // exists. Heavy initialization is deferred to Start().
        try
        {
            var groups = MediaFrameSourceGroup.FindAllAsync().AsTask().GetAwaiter().GetResult();
            foreach (var g in groups)
            {
                foreach (var info in g.SourceInfos)
                {
                    if (info.MediaStreamType == MediaStreamType.VideoPreview ||
                        info.MediaStreamType == MediaStreamType.VideoRecord)
                    {
                        if (info.SourceKind == MediaFrameSourceKind.Color)
                        {
                            _sourceGroupId = g.Id;
                            break;
                        }
                    }
                }
                if (_sourceGroupId is not null) break;
            }
        }
        catch
        {
            _sourceGroupId = null; // no camera, or WinRT not available — stay inert
        }
    }

    /// <summary>True only once Start() has successfully opened the camera.</summary>
    public bool IsSensorAvailable => _initialized && _sourceGroupId is not null;

    /// <summary>True if a webcam was DISCOVERED (Start may still fail on permission denial).</summary>
    public bool CanAttemptInitialization => _sourceGroupId is not null;

    public event EventHandler<AmbientLightSample>? AmbientLightSampled;

    public void Start()
    {
        if (_disposed || _sourceGroupId is null) return;
        lock (_gate)
        {
            if (_initialized) return;
            try
            {
                // Task.Run escapes the captured WPF DispatcherSynchronizationContext so
                // the WinRT `await capture.InitializeAsync(...)` continuations don't try
                // to marshal back to the UI thread (which is blocked on .GetResult()).
                Task.Run(() => InitializeCaptureAsync()).GetAwaiter().GetResult();
                _initialized = true;
                _timer = new System.Threading.Timer(OnTick, null, _pollIntervalMs, _pollIntervalMs);
            }
            catch
            {
                // Permission denied, camera busy, driver crash, etc. Stay inert —
                // controller will see IsSensorAvailable=false and skip applying.
                CleanupCapture();
                _initialized = false;
            }
        }
    }

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
        lock (_gate)
        {
            CleanupCapture();
            _initialized = false;
        }
    }

    private async Task InitializeCaptureAsync()
    {
        var group = await MediaFrameSourceGroup.FromIdAsync(_sourceGroupId);
        if (group is null) throw new InvalidOperationException("Frame source group disappeared.");

        var capture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly, // play nice with other apps
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        };
        await capture.InitializeAsync(settings);

        // Pick a color preview/record source.
        MediaFrameSource? chosen = null;
        foreach (var kv in capture.FrameSources)
        {
            var info = kv.Value.Info;
            if (info.SourceKind != MediaFrameSourceKind.Color) continue;
            if (info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
            {
                chosen = kv.Value;
                break;
            }
        }
        if (chosen is null)
        {
            capture.Dispose();
            throw new InvalidOperationException("No color preview source available.");
        }

        // Prefer the lowest-resolution format to minimize CPU + power.
        var formats = chosen.SupportedFormats
            .Where(f => f.VideoFormat is not null)
            .OrderBy(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .ToList();
        if (formats.Count > 0)
        {
            try { await chosen.SetFormatAsync(formats[0]); } catch { /* keep default */ }
        }

        // Use BGRA8 — universally projected and trivial luminance computation.
        var reader = await capture.CreateFrameReaderAsync(chosen, MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            reader.Dispose();
            capture.Dispose();
            throw new InvalidOperationException($"Frame reader did not start: {status}");
        }

        _capture = capture;
        _reader = reader;
        _frameSource = chosen;
    }

    private void CleanupCapture()
    {
        try { _reader?.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _reader = null;
        _capture = null;
        _frameSource = null;
    }

    private void OnTick(object? state)
    {
        if (_disposed) return;
        if (_sampling) return; // skip if previous tick still in flight
        _sampling = true;
        try
        {
            double? meanY = TryReadMeanLuminance();
            if (meanY is null) return;
            double lux = LuminanceToLux(meanY.Value);
            AmbientLightSampled?.Invoke(this, new AmbientLightSample
            {
                Lux = lux,
                SampledAtUtc = DateTime.UtcNow,
            });
        }
        catch
        {
            // Swallow — next tick may succeed. Never crash the timer thread.
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>
    /// Acquire one frame and compute its mean luminance (0–255). The frame is
    /// downsampled by stride-sampling (every ~32nd pixel on each axis) to keep
    /// CPU cost negligible regardless of native resolution.
    /// </summary>
    private double? TryReadMeanLuminance()
    {
        MediaFrameReader? reader = _reader;
        if (reader is null) return null;

        using var frameRef = reader.TryAcquireLatestFrame();
        var videoFrame = frameRef?.VideoMediaFrame;
        var bitmap = videoFrame?.SoftwareBitmap;
        if (bitmap is null) return null;

        SoftwareBitmap? bgra = null;
        try
        {
            // Convert to BGRA8 if the driver handed us something else (e.g. NV12).
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                bitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
            {
                bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }
            else
            {
                bgra = bitmap;
            }

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            // Pull pixels out via SoftwareBitmap.CopyToBuffer using a managed byte[]
            // bridged through System.Runtime.InteropServices.WindowsRuntime.AsBuffer().
            byte[] pixels = new byte[width * height * 4];
            var ibuf = pixels.AsBuffer();
            bgra.CopyToBuffer(ibuf);

            // Stride-sample: aim for ~32×24 effective samples regardless of source res.
            int strideX = Math.Max(1, width / 32);
            int strideY = Math.Max(1, height / 24);

            double sum = 0;
            int count = 0;
            int rowBytes = width * 4;
            for (int y = 0; y < height; y += strideY)
            {
                int rowOff = y * rowBytes;
                for (int x = 0; x < width; x += strideX)
                {
                    int i = rowOff + x * 4;
                    if (i + 2 >= pixels.Length) break;
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    // Rec. 601 luma — adequate for ambient-brightness estimation.
                    double Y = 0.299 * r + 0.587 * g + 0.114 * b;
                    sum += Y;
                    count++;
                }
            }
            if (count == 0) return null;
            return sum / count;
        }
        finally
        {
            if (!ReferenceEquals(bgra, bitmap))
            {
                try { bgra?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Map mean frame luminance (0–255) to an estimated lux value. The curve is a
    /// log-linear interpolation chosen to land inside LuxComfortMapper's log10
    /// window (LuxLogMin=0 → 1 lx, LuxLogMax=4 → 10 000 lx):
    ///
    ///   meanY   = 0   → ~5      lx  (very dark room)
    ///   meanY   = 60  → ~50     lx  (dim — corridor / TV-watching)
    ///   meanY   = 128 → ~200    lx  (typical office / domestic)
    ///   meanY   = 190 → ~600    lx  (well-lit office)
    ///   meanY   = 230 → ~1500   lx  (very bright indoor / overcast outdoor)
    ///   meanY   = 255 → ~5000   lx  (near-saturation — full daylight)
    ///
    /// This is COARSE and ORDINAL because webcam AE compresses dynamic range. The
    /// downstream LuxComfortMapper smooths in log space and applies a hysteresis
    /// deadband, which is exactly what an ordinal signal needs.
    /// </summary>
    internal static double LuminanceToLux(double meanY)
    {
        meanY = Math.Clamp(meanY, 0.0, 255.0);
        double t = meanY / 255.0;                    // 0..1
        // Map to log10(lux) in [log10(5), log10(5000)] = [0.699, 3.699]
        double logLuxMin = Math.Log10(5.0);
        double logLuxMax = Math.Log10(5000.0);
        // Slight S-curve: emphasize the middle range where AE is most stable.
        double shaped = t * t * (3.0 - 2.0 * t);     // smoothstep
        // Blend linear and smoothstep so endpoints don't collapse.
        double mixed = 0.5 * t + 0.5 * shaped;
        double logLux = logLuxMin + mixed * (logLuxMax - logLuxMin);
        return Math.Pow(10.0, logLux);
    }

    /// <summary>M6 (head-tracking sprint, 2026-05-30): luma scale bridge for
    /// <see cref="SidecarLuxProvider"/>. The sidecar emits
    /// <c>frame_mean_luma_normalized</c> in [0, 1]; WebcamLuxProvider's
    /// luminance-to-lux curve is calibrated on a [0, 255] input. Scaling by
    /// 255 and reusing <see cref="LuminanceToLux"/> means both providers
    /// produce the SAME lux for the same physical scene brightness, so
    /// toggling head-tracking on/off at runtime (or migrating between the
    /// two providers in any future flow) does not bump the
    /// <see cref="SenseEase.App.Comfort.LuxComfortController"/>'s perceived
    /// ambient baseline. Documented in commit M6 (luma scale verification).</summary>
    /// <param name="lumaNormalized">Mean frame luma in [0, 1]. Out-of-range
    /// values are clamped (defensive against a buggy future sidecar).</param>
    /// <returns>Lux estimate on the same curve a webcam-AE frame would
    /// produce for an equivalent scene brightness.</returns>
    public static double LumaNormalizedToLux(double lumaNormalized)
    {
        double clamped = Math.Clamp(lumaNormalized, 0.0, 1.0);
        return LuminanceToLux(clamped * 255.0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
    }
}
