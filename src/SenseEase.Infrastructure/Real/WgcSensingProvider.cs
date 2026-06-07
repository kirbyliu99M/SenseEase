using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT;
using WinRT;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Phase-2.2 real sensing: derives OFI from pixel-diff of captured screen frames via
/// Windows.Graphics.Capture (WGC) + Direct3D11.
///
/// Pipeline:
///   WGC session (primary monitor, item.Size) -> D3D11 staging texture (full resolution) ->
///   CPU Map -> nearest-neighbour step-sample to WgcDownsampleWidth x WgcDownsampleHeight
///   greyscale -> MAD vs previous frame -> EMA smooth -> emit OpticalFlowSampled on UI thread.
///
/// CsWin32 0.3.x generates ID3D11Resource (and related parent interfaces) as plain C#
/// interfaces without [ComImport], so CLR InterfaceMarshaler.ConvertToNative fails when
/// they are passed to CopyResource / Map / Unmap. Fix: bypass CLR marshaling entirely by
/// calling those three methods through the raw D3D11 DeviceContext vtable with IntPtr args.
/// Vtable offsets are stable ABI (IUnknown:0-2, ID3D11DeviceChild:3-6, DeviceContext:7+).
/// </summary>
public sealed class WgcSensingProvider : ISensingProvider, IDisposable
{
    // UnmanagedType.HString is .NET Framework-only; create HSTRING manually via combase.dll.
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string? src, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        in Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    // ── D3D11 vtable indices (ID3D11DeviceContext inherits IUnknown+ID3D11DeviceChild) ──────
    // IUnknown        : 0-2  (QI, AddRef, Release)
    // ID3D11DeviceChild: 3-6  (GetDevice, GetPrivateData, SetPrivateData, SetPrivateDataInterface)
    // ID3D11DeviceContext starts at 7:
    //   +7  VSSetConstantBuffers … +13 Draw  →  Map=14, Unmap=15
    //   +40 CopySubresourceRegion  →  CopyResource=47
    private const int VtblMap          = 14;
    private const int VtblUnmap        = 15;
    private const int VtblCopyResource = 47;

    private static readonly Guid IID_ID3D11Resource =
        new("dc8e63f3-d12b-4952-b47b-5e45026a862d");

    // ── Diagnostic log ───────────────────────────────────────────────────────────────────────
    // Phase 4 (UVHS Playbook clause 5): routed through WgcDiagLog for the
    // retry-on-IOException + orthogonal-probe sequence prefix + independent
    // loss sink. Path constant kept here for the OFI trace reset below.
    private static readonly string DiagFile = WgcDiagLog.DiagFile;

    private static void WgcLog(string msg)
    {
        Debug.WriteLine($"[WGC] {msg}");
        WgcDiagLog.Append($"{DateTime.UtcNow:HH:mm:ss.fff}Z {msg}"); // H41: UTC, not local
    }

    // Returns a raw AddRef'd interface pointer for the given IID; caller must Release.
    private static IntPtr GetRawComPtr(object comObj, Guid iid)
    {
        IntPtr unk = Marshal.GetIUnknownForObject(comObj);
        try
        {
            int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
                throw new COMException($"QI for {iid} hr=0x{hr:X8}", hr);
            return ptr;
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    private static void SafeRelease(ref IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) { Marshal.Release(ptr); ptr = IntPtr.Zero; }
    }

    // ── Instance state ───────────────────────────────────────────────────────────────────────
    private readonly SynchronizationContext? _ctx;
    private readonly Timer _timer;
    private bool   _running;
    // EM3: serialises Stop()'s COM teardown against an in-flight OnTick so the
    // capture never dereferences freed pointers. EM4: _faulted coalesces the
    // single-shot fault-recovery re-init.
    private readonly object _lifecycleLock = new();
    private volatile bool _faulted;
    private const int WgcFaultRecoveryDelayMs = 500;

    // D3 liveness contract: continuous sensing-liveness state. _unhealthy is the
    // edge-trigger latch so SensingHealthChanged fires only on TRANSITIONS, not
    // every frame. The liveness watchdog (OnLivenessWatchdog) rearms forever —
    // unlike the one-shot Resume watchdog — and keys off LOOP liveness (_lastPollAt,
    // stamped every tick) rather than new-frame arrival. WGC emits no new frames on
    // an idle/static screen (also monitor-sleep, lock-screen, RDP), which is healthy,
    // not a stall — so only a wedged capture loop (OnTick stops firing) trips the
    // self-heal / Faulted thresholds. Throwing faults (GPU TDR / device-removed) are
    // surfaced + recovered by the OnTick catch instead.
    private readonly Timer _livenessTimer;
    private volatile bool _unhealthy;
    private const int SensingStalledThresholdMs = 3000; // silent self-heal trigger
    private const int SensingFaultedThresholdMs = 6000; // surface Faulted if still stale
    private const int LivenessWatchdogIntervalMs = 1000;
    private byte[]? _prevFrame;
    private double  _smoothedOfi;
    private readonly double[] _ofiHistory = new double[SenseDefaults.VimsMedianTaps];
    private int _ofiHistoryFilled;   // how many slots are populated (until the buffer is full)
    private int _ofiHistoryHead;     // ring-buffer write index

    // ── Fix #5: ping-pong buffers (allocated once in Start, toggled per tick) ──────────────
    private byte[]? _bufA;
    private byte[]? _bufB;
    private bool _useBufA; // which buffer is written this tick; toggles each call
    // Returned for all early-outs where there is no frame data. Zero-filled, never mutated.
    private static readonly byte[] EmptyFrameSentinel =
        new byte[SenseDefaults.WgcDownsampleWidth * SenseDefaults.WgcDownsampleHeight];

    // ── Fix #6: cached nearest-neighbour index tables (rebuilt only on srcW/srcH change) ───
    private int[]? _srcXIndex;  // dstX -> byte offset within a source row (srcX * 4 for BGRA)
    private int[]? _srcYIndex;  // dstY -> source row index (in pixels)
    private int _indexedSrcW = -1, _indexedSrcH = -1;

    // ── Pop-up sprint Task 1 (Option B): bar-region pixel mask ─────────────────────────────
    // Caller sets a monitor-coords rect (physical pixels, origin at top-left of primary
    // monitor). After the downsample, the corresponding cells in the 160×90 greyscale
    // buffer are zeroed in BOTH `current` and the previous frame so MAD doesn't see a
    // ghost edge against the bar pixels. Default null = no masking, identical to pre-
    // Task-1 behaviour. The reference is volatile so the OnTick thread sees setter
    // writes from the UI thread without a lock; the holder is immutable so the read
    // can dereference once and reuse the captured snapshot for the whole tick.
    private volatile MaskRect? _excludeRect;
    private sealed record MaskRect(int Left, int Top, int Width, int Height);
    /// <summary>Set a monitor-coords rectangle (physical pixels) whose downsampled
    /// cells will be zeroed out before MAD. Used by Pop-up sprint Task 1 to prove
    /// the bar's pixels can be excluded from the OFI computation. Pass null to
    /// disable. Thread-safe; OnTick picks up the new value on its next tick.</summary>
    public void SetMonitorExcludeRect(int? left, int? top, int? width, int? height)
    {
        if (left is null || top is null || width is null || height is null
            || width.Value <= 0 || height.Value <= 0)
        {
            _excludeRect = null;
            return;
        }
        _excludeRect = new MaskRect(left.Value, top.Value, width.Value, height.Value);
    }
    /// <summary>Current monitor-coords exclude rect (or null if not set). For diagnostics.</summary>
    public (int Left, int Top, int Width, int Height)? CurrentExcludeRect
        => _excludeRect is { } r ? (r.Left, r.Top, r.Width, r.Height) : null;

    // ── Pop-up sprint Task 1: optional OFI trace log ───────────────────────────────────────
    // When env var SENSEEASE_WGC_BAR_VALIDATION=1, append "ms,ofi,maskOn" per tick to
    // %TEMP%/wgc_ofi_trace.txt. Zero overhead in production (env var read once at Start).
    private bool   _ofiTraceEnabled;
    private string _ofiTracePath = string.Empty;

    // M1 calibration: dev-gated raw-MAD capture. When env SENSEEASE_MAD_CALIBRATION=1,
    // append "epoch_ms,raw_mad" per tick to %TEMP%/wgc_mad_calibration_trace.csv. raw_mad
    // is the PRE-norm-factor, PRE-clamp pixel-diff — the genuine measurement M1's
    // MadCalibrationReducer / NormFactorRecommender (SenseEase.Core.Sensing) consume to
    // re-derive WgcMadNormFactor from real scenes. Zero overhead in production (env read once).
    private bool   _madCalibrationEnabled;
    private string _madCalibrationPath = string.Empty;

    // ── Fix #7: cached SendOrPostCallback to avoid per-tick closure allocation ─────────────
    private OpticalFlowSample _pendingSample;
    private System.Threading.SendOrPostCallback? _raiseSampleCallback;

    // D3D11 / WGC managed objects
    private ID3D11Device?          _d3dDevice;
    private ID3D11DeviceContext?   _d3dContext;
    private GraphicsCaptureItem?   _captureItem;
    private GraphicsCaptureSession? _captureSession;
    private Direct3D11CaptureFramePool? _framePool;
    private ID3D11Texture2D?       _stagingTexture;
    private IDirect3DDevice?       _winrtDevice;

    // Raw COM interface pointers cached at Start(); bypasses CsWin32 marshaling limitations.
    private IntPtr _ctxPtr;        // ID3D11DeviceContext*
    private IntPtr _stagingResPtr; // ID3D11Resource* for the staging texture

    private int _contentWidth;
    private int _contentHeight;

    public event EventHandler<OpticalFlowSample>? OpticalFlowSampled;

    /// <summary>D3 liveness contract: raised (edge-triggered) when sensing health
    /// flips. <c>false</c> = the pipeline died unexpectedly (OnTick threw, or the
    /// liveness watchdog saw no real frame past the faulted threshold);
    /// <c>true</c> = a fresh real frame restored sensing after a fault. Marshalled
    /// onto the captured SynchronizationContext (UI thread) like OpticalFlowSampled,
    /// so subscribers can touch UI state directly. Never fires on an intentional
    /// Stop()/Pause() — only on UNEXPECTED loss.</summary>
    public event Action<bool>? SensingHealthChanged;

    // overlay-bar-redesign D3: Pause/Resume support. _paused is volatile so
    // the OnTick thread observes UI-thread writes without a lock. _resumedAt
    // and _watchdogScheduled drive a one-shot watchdog that re-inits the
    // capture session if the framepool doesn't recover after Resume — guards
    // against the (rare) case where pausing then resuming leaves WGC in a
    // state where TryGetNextFrame keeps returning null.
    private volatile bool _paused;
    private DateTime _resumedAt;
    private bool _watchdogScheduled;
    private DateTime _lastFrameProcessedAt; // updated each successful OnTick (real frame) — clears faults via MarkHealthy
    private DateTime _lastPollAt;           // updated every non-throwing OnTick (loop-liveness clock the watchdog keys off)

    // v0.8 hotfix: warm-up suppression. User reported that at app startup
    // the VIMS pressure spiked (~60% FOV reduction visible as a dimmed
    // overlay) for the first second or two. Root cause: WGC's initial
    // frames are inherently noisy (first frame compares against null
    // _prevFrame → 0, but the SECOND frame computes a large MAD against
    // _prevFrame that hasn't seen real motion yet; median filter buffer
    // isn't full so spurious values pass through directly; EMA smoothing
    // starts from 0 so even partial samples register strongly). The
    // LeakyIntegrator accumulates this noise into pressure before the
    // pipeline stabilises.
    //
    // Fix: emit OFI=0 for the first WarmupDurationSec seconds after Start.
    // _smoothedOfi + _prevFrame are also held at neutral during warmup so
    // the first POST-warmup frame computes against a clean baseline.
    private const double WarmupDurationSec = 2.0;
    private DateTime _warmupUntilUtc;

    /// <summary>overlay-bar-redesign D3: true while sensing is paused via
    /// <see cref="Pause"/>. Exposed for tests; production reads it via the
    /// internal frame-callback short-circuit.</summary>
    public bool IsPaused => _paused;

    public WgcSensingProvider()
    {
        _ctx          = SynchronizationContext.Current;
        _timer        = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        _livenessTimer = new Timer(OnLivenessWatchdog, null, Timeout.Infinite, Timeout.Infinite); // D3
        _pendingSample = new OpticalFlowSample();
        // Fix #7: cache the callback once so OnTick never allocates a closure.
        _raiseSampleCallback = _ => OpticalFlowSampled?.Invoke(this, _pendingSample);
    }

    /// <summary>v0.8 hotfix follow-up: full tear-down of the WGC capture
    /// session, not just a flag short-circuit. User feedback was that the
    /// Windows yellow capture indicator staying visible during "Power Save"
    /// contradicts the toggle's mental model of "off". Pause now calls
    /// <see cref="Stop"/> which releases the GraphicsCaptureSession + D3D11
    /// device + COM pointers — yellow border disappears immediately.
    /// <para>The <c>_paused</c> flag is kept as a redundant short-circuit
    /// (set BEFORE Stop) so any in-flight OnTick callback exits early
    /// before Stop releases the pointers it would otherwise dereference.</para>
    /// <para>Idempotent — Pause/Pause is a no-op (Stop checks _running).</para></summary>
    public void Pause()
    {
        _paused = true;
        Stop();
    }

    /// <summary>v0.8 hotfix follow-up: full fresh init on resume. Calls
    /// <see cref="Start"/> which rebuilds the WGC session, D3D11 device,
    /// and staging texture from scratch. ~1-2 s cost is acceptable for an
    /// intentional user action; the yellow capture indicator reappears
    /// at this point (which is correct — sensing IS resuming).
    /// <para>Spike-detector reset is NOT performed here: the SpikeDetector
    /// lives in the consumer (MainViewModel) rather than the provider.
    /// MainWindow.ApplyPowerSave wires <c>_viewModel.ResetSpikeDetector()</c>
    /// on the disable path.</para>
    /// <para>The 2 s frame-arrival watchdog is still scheduled even though
    /// the fresh Start makes the Power-Save resume path itself self-healing.
    /// The watchdog remains useful for non-Power-Save OS-level invalidation
    /// scenarios (lock-screen recovery, monitor sleep wake, RDP reconnect)
    /// where a session that was alive can stop yielding frames silently.</para>
    /// <para>Idempotent — Resume/Resume is a no-op (Start checks _running).</para></summary>
    public void Resume()
    {
        _paused = false;
        Start();
        _resumedAt = DateTime.UtcNow;
        ScheduleResumeWatchdog();
    }

    private void ScheduleResumeWatchdog()
    {
        if (_watchdogScheduled) return;
        _watchdogScheduled = true;
        // Fire once after 2 s. Using a separate Timer here (not the OnTick
        // capture timer) keeps the watchdog independent of the capture
        // cadence and avoids reentering the capture-timer callback path.
        var watchdog = new Timer(_ =>
        {
            try
            {
                if (_paused) return; // user paused again before the watchdog fired
                bool frameSeenSinceResume = _lastFrameProcessedAt >= _resumedAt;
                if (frameSeenSinceResume) return;
                if (!_running) return;
                WgcLog("Resume watchdog: no frame seen 2 s after Resume → Stop/Start re-init.");
                try
                {
                    Stop();
                    Start();
                }
                catch (Exception ex)
                {
                    WgcLog($"Resume watchdog re-init failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                _watchdogScheduled = false;
            }
        }, null, 2000, Timeout.Infinite);
        // The closure pins `watchdog` for the lifetime of the callback so
        // it doesn't get GC'd before firing.
        _ = watchdog;
    }

    /// <summary>EM4: recover from a transient OnTick fault. Disarms the timer and
    /// schedules a single race-free Stop()+Start() re-init after a short delay, so a
    /// GPU TDR / display-mode change / RDP reconnect no longer leaves WGC sensing
    /// (and the FOV mask) permanently dead. Re-entrant faults during the delay are
    /// coalesced by <c>_faulted</c>.</summary>
    private void ScheduleFaultRecovery()
    {
        if (_faulted) return;
        _faulted = true;
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WgcFaultRecoveryDelayMs).ConfigureAwait(false);
                if (!_paused)
                {
                    WgcLog("OnTick fault → scheduled Stop/Start re-init (EM4).");
                    Stop();
                    Start();
                }
            }
            catch (Exception ex)
            {
                WgcLog($"Fault-recovery re-init failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally { _faulted = false; }
        });
    }

    /// <summary>D3 liveness watchdog: rearming staleness check. Distinct from the
    /// one-shot Resume watchdog. Heal-first / surface-second so transient hiccups
    /// (monitor wake, brief TDR) never reach the user: at the stall threshold it
    /// triggers the same silent Stop()+Start() self-heal the fault path uses; only
    /// if the capture loop is STILL unresponsive at the (larger) faulted threshold
    /// does it surface a fault. Keys off _lastPollAt (loop liveness), so an idle
    /// screen with no new frames is NOT mistaken for a stall. Suppressed while
    /// paused, warming up, or before the first tick.
    /// Never throws.</summary>
    private void OnLivenessWatchdog(object? _)
    {
        try
        {
            if (!_running || _paused) return;                 // not actively sensing
            if (DateTime.UtcNow < _warmupUntilUtc) return;    // pipeline still settling
            if (_lastPollAt == default) return;               // no tick yet (Start in progress)

            // Key off the LOOP-liveness poll, not new-frame arrival. WGC produces no
            // new frames on an idle/static screen (or monitor-sleep / lock / RDP), so
            // a frame-arrival watchdog would Stop()+Start() every few seconds on a
            // quiet desktop — the "capture flaps on/off" bug. A stale poll instead
            // means OnTick itself stopped firing (timer wedged / callback hung), the
            // real fault worth healing. Throwing faults (GPU TDR / device-removed)
            // are surfaced + recovered by the OnTick catch, independent of this clock.
            double ageMs = (DateTime.UtcNow - _lastPollAt).TotalMilliseconds;
            if (ageMs > SensingFaultedThresholdMs)
            {
                WgcLog($"Liveness watchdog: capture loop unresponsive for {ageMs:F0} ms (> {SensingFaultedThresholdMs}) → surfacing Faulted.");
                MarkUnhealthy();
                ScheduleFaultRecovery();  // keep attempting recovery (coalesced by _faulted)
            }
            else if (ageMs > SensingStalledThresholdMs)
            {
                WgcLog($"Liveness watchdog: capture loop unresponsive for {ageMs:F0} ms (> {SensingStalledThresholdMs}) → silent self-heal.");
                ScheduleFaultRecovery();  // heal first, do NOT surface yet
            }
        }
        catch { /* a watchdog must never throw */ }
    }

    /// <summary>D3: edge-triggered transition to unhealthy. Posts SensingHealthChanged(false)
    /// once on the captured context; further calls while already unhealthy are no-ops.</summary>
    private void MarkUnhealthy()
    {
        if (_unhealthy) return;
        _unhealthy = true;
        PostHealth(false);
    }

    /// <summary>D3: edge-triggered transition back to healthy. Posts SensingHealthChanged(true)
    /// once on recovery; no-op if not currently unhealthy.</summary>
    private void MarkHealthy()
    {
        if (!_unhealthy) return;
        _unhealthy = false;
        PostHealth(true);
    }

    /// <summary>Marshal the health event onto the captured SynchronizationContext
    /// (UI thread in production) like OpticalFlowSampled, so subscribers touch UI
    /// state safely. Faults are rare → the per-event closure alloc is negligible.</summary>
    private void PostHealth(bool healthy)
    {
        var handler = SensingHealthChanged;
        if (handler is null) return;
        if (_ctx != null) _ctx.Post(_ => handler(healthy), null);
        else handler(healthy);
    }

    public void Start()
    {
        if (_running) return;

        // Phase 4 (UVHS Playbook clause 5): no longer truncates DiagFile —
        // WriteHeader appends a delimiter so the full WGC lifecycle history
        // (across multiple Start/Stop rounds in a UVHS session) is preserved.
        WgcDiagLog.WriteHeader($"WGC Start {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z"); // H41: UTC

        // Pop-up sprint Task 1: enable OFI trace log when SENSEEASE_WGC_BAR_VALIDATION=1.
        // Trace path is %TEMP%/wgc_ofi_trace.txt; header line written here, per-tick
        // CSV lines written from OnTick. Env var read ONCE here so toggling it
        // mid-session requires a restart (intentional — avoids per-tick env-var lookups).
        _ofiTraceEnabled = string.Equals(
            Environment.GetEnvironmentVariable("SENSEEASE_WGC_BAR_VALIDATION"),
            "1", StringComparison.Ordinal);
        if (_ofiTraceEnabled)
        {
            _ofiTracePath = Path.Combine(Path.GetTempPath(), "wgc_ofi_trace.txt");
            // Phase 4: OFI trace IS a per-session file (truncation intentional),
            // but goes through ResetSessionFile so a sharing violation no longer
            // silently drops the header — failure is surfaced to wgc_diag.lost.log.
            WgcDiagLog.ResetSessionFile(_ofiTracePath,
                $"# wgc_ofi_trace start {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n" + // H41: UTC
                "epoch_ms,smoothed_ofi,mask_left,mask_top,mask_w,mask_h\n");
        }

        // M1 calibration capture (dev-gated; read once at Start like the OFI trace above).
        _madCalibrationEnabled = string.Equals(
            Environment.GetEnvironmentVariable("SENSEEASE_MAD_CALIBRATION"),
            "1", StringComparison.Ordinal);
        if (_madCalibrationEnabled)
        {
            _madCalibrationPath = Path.Combine(Path.GetTempPath(), "wgc_mad_calibration_trace.csv");
            WgcDiagLog.ResetSessionFile(_madCalibrationPath,
                $"# wgc_mad_calibration start {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n" + // H41: UTC
                "epoch_ms,raw_mad\n");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            WgcLog("GraphicsCaptureSession not supported on this OS.");
            return;
        }

        unsafe
        {
            try
            {
                // Step 1: D3D11 hardware device
                WgcLog("Step 1: D3D11CreateDevice...");
                int hr = PInvoke.D3D11CreateDevice(
                    null,
                    D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                    new HMODULE(IntPtr.Zero),
                    D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    null, 0, 7,
                    out _d3dDevice, null, out _d3dContext);
                if (hr < 0 || _d3dDevice == null || _d3dContext == null)
                    throw new COMException($"D3D11CreateDevice hr=0x{hr:X8}", hr);
                WgcLog("Step 1: OK");

                // Step 2: Wrap D3D11 device in WinRT IDirect3DDevice
                WgcLog("Step 2: Wrap IDirect3DDevice...");
                var dxgiDevice = (Windows.Win32.Graphics.Dxgi.IDXGIDevice)(object)_d3dDevice;
                hr = PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice,
                    out Windows.Win32.System.WinRT.IInspectable inspectableDevice);
                if (hr < 0)
                    throw new COMException($"CreateDirect3D11DeviceFromDXGIDevice hr=0x{hr:X8}", hr);

                IntPtr inspPtr = Marshal.GetIUnknownForObject(inspectableDevice);
                try   { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspPtr); }
                finally { Marshal.Release(inspPtr); }
                WgcLog("Step 2: OK");

                // Step 3: GraphicsCaptureItem for primary monitor
                WgcLog("Step 3: GraphicsCaptureItem...");
                var hmon = PInvoke.MonitorFromPoint(
                    new System.Drawing.Point(0, 0),
                    Windows.Win32.Graphics.Gdi.MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

                string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
                int wsHr = WindowsCreateString(className, className.Length, out IntPtr classHStr);
                if (wsHr < 0) throw new COMException($"WindowsCreateString hr=0x{wsHr:X8}", wsHr);
                Guid factoryIid = typeof(IGraphicsCaptureItemInterop).GUID;
                hr = RoGetActivationFactory(classHStr, in factoryIid, out object factoryObj);
                _ = WindowsDeleteString(classHStr);
                if (hr < 0) throw new COMException($"RoGetActivationFactory hr=0x{hr:X8}", hr);

                var interop = (IGraphicsCaptureItemInterop)factoryObj;
                Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                interop.CreateForMonitor(hmon, &itemIid, out object itemObj);

                IntPtr itemPtr = Marshal.GetIUnknownForObject(itemObj);
                try   { _captureItem = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
                WgcLog("Step 3: OK");

                // Step 4: Record monitor resolution
                var contentSize = _captureItem.Size;
                _contentWidth  = contentSize.Width;
                _contentHeight = contentSize.Height;
                WgcLog($"Step 4: Monitor resolution = {_contentWidth}x{_contentHeight}");

                // Step 5: Frame pool + capture session
                WgcLog("Step 5: CreateFreeThreaded frame pool...");
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);

                _captureSession = _framePool.CreateCaptureSession(_captureItem);
                _captureSession.IsCursorCaptureEnabled = false;
                _captureSession.StartCapture();
                WgcLog("Step 5: OK — capture started (yellow border visible)");

                // Step 6: Staging texture (CPU-readable, full monitor resolution)
                WgcLog($"Step 6: CreateTexture2D {_contentWidth}x{_contentHeight} STAGING...");
                var desc = new D3D11_TEXTURE2D_DESC
                {
                    Width      = (uint)_contentWidth,
                    Height     = (uint)_contentHeight,
                    MipLevels  = 1,
                    ArraySize  = 1,
                    Format     = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                    Usage      = D3D11_USAGE.D3D11_USAGE_STAGING,
                    BindFlags  = 0,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                    MiscFlags  = 0,
                };
                _d3dDevice.CreateTexture2D(desc, null, out _stagingTexture);
                if (_stagingTexture == null)
                    throw new InvalidOperationException("CreateTexture2D returned null.");
                WgcLog("Step 6: OK");

                // Step 7: Cache raw COM pointers — bypasses CsWin32 [ComImport]-less marshaling.
                WgcLog("Step 7: Caching raw vtable pointers...");
                _ctxPtr        = GetRawComPtr(_d3dContext,    typeof(ID3D11DeviceContext).GUID);
                _stagingResPtr = GetRawComPtr(_stagingTexture, IID_ID3D11Resource);
                WgcLog($"Step 7: OK  ctxPtr=0x{_ctxPtr:X}  stagingResPtr=0x{_stagingResPtr:X}");
            }
            catch (COMException ex)
            {
                WgcLog($"FAILED (COMException): 0x{ex.HResult:X8} — {ex.Message}");
                Stop();
                return;
            }
            catch (Exception ex)
            {
                WgcLog($"FAILED ({ex.GetType().Name}): {ex.Message}\n{ex.StackTrace}");
                Stop();
                return;
            }
        }

        _running     = true;
        _prevFrame   = null;
        _smoothedOfi = 0;
        Array.Clear(_ofiHistory);
        _ofiHistoryFilled = 0;
        _ofiHistoryHead   = 0;
        // v0.8 hotfix: arm the warm-up window. OnTick will suppress OFI
        // emission (force OFI=0 + reset _prevFrame each tick) until this
        // deadline passes, giving the pipeline time to stabilise without
        // accumulating noise into the LeakyIntegrator → no startup
        // pressure spike → overlay stays at neutral FOV until real
        // measurements are available.
        _warmupUntilUtc = DateTime.UtcNow.AddSeconds(WarmupDurationSec);
        // Seed both liveness clocks at Start so the first post-warmup watchdog tick
        // never sees a stale poll (the watchdog is suppressed until warmup ends, and
        // OnTick stamps _lastPollAt every tick from here on).
        _lastPollAt = _lastFrameProcessedAt = DateTime.UtcNow;
        WgcLog($"Start complete — timer running. Warmup until {_warmupUntilUtc:HH:mm:ss.fff}");
        _timer.Change(SenseDefaults.WgcCaptureIntervalMs, SenseDefaults.WgcCaptureIntervalMs);
        // D3: arm the continuous liveness watchdog alongside the capture timer.
        _livenessTimer.Change(LivenessWatchdogIntervalMs, LivenessWatchdogIntervalMs);
    }

    public void Stop()
    {
        // EM3: hold the lifecycle lock so we never free COM/D3D pointers while an
        // OnTick is mid-capture (OnTick takes the same lock). Idempotent — every
        // release below is null-guarded, so a second Stop() (or a Stop after a fault
        // recovery / Dispose) is a safe no-op. The timer.Change is try-guarded
        // because Dispose() may already have disposed the timer.
        lock (_lifecycleLock)
        {
            _running = false;
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* may be disposed */ }
            try { _livenessTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* may be disposed */ } // D3

            // Release cached raw pointers first.
            SafeRelease(ref _ctxPtr);
            SafeRelease(ref _stagingResPtr);

            // Dispose WinRT objects.
            try { _captureSession?.Dispose(); } catch { }
            try { _framePool?.Dispose();      } catch { }
            try { _winrtDevice?.Dispose();    } catch { }

            // Release CsWin32 COM objects (only if proper RCW).
            try { if (_stagingTexture != null && Marshal.IsComObject(_stagingTexture)) Marshal.ReleaseComObject(_stagingTexture); } catch { }
            try { if (_d3dContext     != null && Marshal.IsComObject(_d3dContext))     Marshal.ReleaseComObject(_d3dContext);     } catch { }
            try { if (_d3dDevice      != null && Marshal.IsComObject(_d3dDevice))      Marshal.ReleaseComObject(_d3dDevice);     } catch { }

            _captureSession = null;
            _framePool      = null;
            _winrtDevice    = null;
            _captureItem    = null;
            _stagingTexture = null;
            _d3dContext     = null;
            _d3dDevice      = null;
        }
    }

    /// <summary>Releases all D3D11/WGC resources + the capture timer. Safe to call
    /// multiple times (NE11: the timer was previously never disposed → leak).</summary>
    public void Dispose()
    {
        Stop();
        try { _timer.Dispose(); } catch { }
        try { _livenessTimer.Dispose(); } catch { } // D3 (NE11-style: dispose the watchdog timer too)
    }

    private void OnTick(object? state)
    {
        if (!_running) return;
        // overlay-bar-redesign D3: Power Save short-circuit. Skip frame
        // capture entirely while paused so the WGC pipeline goes quiet
        // without disposing the capture session (cheap to resume).
        if (_paused) return;
        if (_contentWidth == 0 || _contentHeight == 0) return;

        // Liveness poll stamp: mark that the capture loop is RUNNING this tick,
        // independent of whether a new frame arrived. WGC only delivers frames on
        // screen CHANGE, so an idle/static screen (also monitor-sleep, lock-screen,
        // RDP) legitimately yields no new frames — that must NOT look like a stall.
        // The watchdog keys off this poll time; _lastFrameProcessedAt (real frame)
        // is kept only to clear faults via MarkHealthy. A genuinely wedged loop
        // stops stamping → watchdog recovers; throwing faults hit the catch below.
        _lastPollAt = DateTime.UtcNow;

        // v0.8 hotfix: warm-up suppression. While the pipeline is settling
        // (first WarmupDurationSec after Start), emit OFI=0 and keep
        // internal state at zero so the LeakyIntegrator can't accumulate
        // pressure from the initial noisy frames. Without this, the
        // FOV-reduction overlay would visibly dim ~60% of the screen at
        // every app startup until samples stabilised — looks like a bug.
        if (DateTime.UtcNow < _warmupUntilUtc)
        {
            _smoothedOfi = 0;
            _prevFrame   = null;  // also reset so first post-warmup frame
                                  // has a clean previous-frame slate
            _pendingSample = new OpticalFlowSample
            {
                OpticalFlowIndex = 0,
                SampledAt        = DateTime.UtcNow,
            };
            if (_ctx != null && _raiseSampleCallback != null)
                _ctx.Post(_raiseSampleCallback, null);
            else
                OpticalFlowSampled?.Invoke(this, _pendingSample);
            return;
        }

        // EM3: serialise the capture (which dereferences the cached COM/D3D
        // pointers) against Stop()'s teardown. Stop() takes the same lock before
        // SafeRelease, so a tick either runs to completion before the pointers are
        // freed (Stop waits) or sees _running==false after Stop and bails — no
        // use-after-free on a Power-Save toggle / mode swap / Reset / watchdog.
        lock (_lifecycleLock)
        {
        if (!_running || _paused) return; // re-check after acquiring (Stop may have run)
        try
        {
            byte[] current = CaptureAndDownsample(out bool realFrame);
            // Path Z bug-fix 2: only stamp when a real frame was actually
            // produced. Stamping on every tick (including the null-frame
            // fallback that returns _prevFrame or EmptyFrameSentinel) means
            // a stuck capture session never trips the watchdog.
            if (realFrame)
            {
                _lastFrameProcessedAt = DateTime.UtcNow;
                MarkHealthy(); // D3: a fresh real frame clears any prior fault (edge-triggered)
            }

            double rawMad = 0;
            double rawOfi = 0;
            bool haveMad = _prevFrame != null && current.Length == _prevFrame.Length;
            if (haveMad)
            {
                rawMad = ComputeMad(current, _prevFrame!);
                rawOfi = rawMad * SenseDefaults.WgcMadNormFactor;
            }

            // M1 calibration: capture the raw (pre-norm-factor, pre-clamp) MAD per tick when
            // SENSEEASE_MAD_CALIBRATION=1. Best-effort; a transient IO error drops one sample.
            if (_madCalibrationEnabled && haveMad)
            {
                try
                {
                    File.AppendAllText(_madCalibrationPath,
                        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{rawMad:F4}\n");
                }
                catch { /* calibration trace is best-effort */ }
            }

            _prevFrame   = current;

            // OS-CHROME MOTION EXCLUSION (Phase 2.2.1):
            // Suppress raw MAD-derived OFI whenever Windows itself is generating the on-screen
            // motion (Alt+Tab, menu, window move, notification, typing). Zeroing BEFORE the
            // median+EMA chain lets the median fill with zeros during the suppression window
            // so onsets recover cleanly once the user-driven event ends.
            if (WindowsActivityFilter.Instance.ShouldSuppressOfi)
                rawOfi = 0;

            double clamped  = Math.Clamp(rawOfi, 0, 150);
            double filtered = PushAndMedian(clamped);
            _smoothedOfi    = (SenseDefaults.CursorEmaAlpha * _smoothedOfi)
                            + ((1.0 - SenseDefaults.CursorEmaAlpha) * filtered);

            var sample = new OpticalFlowSample
            {
                OpticalFlowIndex = _smoothedOfi,
                SampledAt        = DateTime.UtcNow,
            };

            // Pop-up sprint Task 1: per-tick trace line when validation env var is on.
            // CSV: epoch_ms,smoothed_ofi,mask_left,mask_top,mask_w,mask_h (last 4 blank
            // when mask is null). File I/O is fire-and-forget; a transient error
            // (file locked, etc.) drops one sample, never crashes the engine.
            if (_ofiTraceEnabled)
            {
                try
                {
                    long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var er = _excludeRect;
                    string line = er is null
                        ? $"{ms},{_smoothedOfi:F4},,,,\n"
                        : $"{ms},{_smoothedOfi:F4},{er.Left},{er.Top},{er.Width},{er.Height}\n";
                    File.AppendAllText(_ofiTracePath, line);
                }
                catch { /* trace is best-effort */ }
            }

            // Use cached SendOrPostCallback + _pendingSample field to avoid per-tick
            // closure allocations (~30 obj/sec at 15 Hz). Race trade-off accepted:
            // back-to-back ticks before dispatcher drains → latest sample wins (intermediate
            // dropped). Engine's leaky integrator absorbs single skipped samples cleanly.
            _pendingSample = sample;
            if (_ctx != null && _raiseSampleCallback != null)
                _ctx.Post(_raiseSampleCallback, null);
            else
                OpticalFlowSampled?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            // EM4: a transient OnTick fault (GPU TDR, display-mode change, RDP
            // reconnect) must NOT permanently disable sensing. Recover via a
            // race-free Stop()+Start() re-init instead of a terminal Stop().
            WgcLog($"OnTick FATAL ({ex.GetType().Name}): {ex.Message}\n{ex.StackTrace}");
            MarkUnhealthy();          // D3: a thrown OnTick is never transient-silent — surface immediately (the highest-value line)
            ScheduleFaultRecovery();
        }
        } // close EM3 lock (_lifecycleLock)
    }

    private byte[] CaptureAndDownsample(out bool realFrame)
    {
        realFrame = false;
        int dstW = SenseDefaults.WgcDownsampleWidth;
        int dstH = SenseDefaults.WgcDownsampleHeight;

        if (_framePool == null || _ctxPtr == IntPtr.Zero || _stagingResPtr == IntPtr.Zero)
            return EmptyFrameSentinel;  // shared zero buffer, never mutated

        var frame = _framePool.TryGetNextFrame();
        if (frame == null)
            return _prevFrame ?? EmptyFrameSentinel;

        // Ping-pong buffer: alternate writing into _bufA / _bufB so the previous tick's
        // _prevFrame (which aliases one of them) is never overwritten this tick.
        // Lazy-init on first call since dstW*dstH is constant from SenseDefaults.
        if (_bufA is null || _bufB is null)
        {
            int n = dstW * dstH;
            _bufA = new byte[n];
            _bufB = new byte[n];
        }
        byte[] result = _useBufA ? _bufA : _bufB;
        _useBufA = !_useBufA;

        // Hold these outside the try so the finally can release them unconditionally —
        // leaving any of them un-released at 15 Hz piles up +1 refs on the frame's GPU
        // texture, defeats the FramePool's 2-buffer recycling, and grows native memory
        // unbounded until GC pressure stalls the app.
        IDirect3DSurface? surface = null;
        IDirect3DDxgiInterfaceAccess? dxgiAccess = null;
        object? texObj = null;
        IntPtr gpuResPtr = IntPtr.Zero;

        try
        {
            // Guard: CopyResource requires dimensions to match exactly.
            var frameSize = frame.ContentSize;
            if (frameSize.Width != _contentWidth || frameSize.Height != _contentHeight)
            {
                WgcLog($"Frame size mismatch: {frameSize.Width}x{frameSize.Height} vs {_contentWidth}x{_contentHeight}");
                return _prevFrame ?? EmptyFrameSentinel;
            }

            unsafe
            {
                // QI the frame surface to IDirect3DDxgiInterfaceAccess, then get the GPU texture.
                surface    = frame.Surface;
                dxgiAccess = surface.As<IDirect3DDxgiInterfaceAccess>();
                if (dxgiAccess == null) return _prevFrame ?? EmptyFrameSentinel;

                Guid texIid = typeof(ID3D11Texture2D).GUID;
                dxgiAccess.GetInterface(&texIid, out texObj);

                // Get ID3D11Resource* raw pointer for the GPU frame texture.
                gpuResPtr = GetRawComPtr(texObj, IID_ID3D11Resource);

                try
                {
                    // ── Raw vtable calls — bypass CsWin32's non-[ComImport] ID3D11Resource ──

                    void** vtbl = *(void***)_ctxPtr;

                    // CopyResource(pDst, pSrc) — vtable[47]
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)
                        vtbl[VtblCopyResource])(_ctxPtr, _stagingResPtr, gpuResPtr);

                    // Map(pResource, Subresource, MapType, MapFlags, pMapped) — vtable[14]
                    D3D11_MAPPED_SUBRESOURCE mapped;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)
                        vtbl[VtblMap])(_ctxPtr, _stagingResPtr, 0, (uint)D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                    if (hr < 0) throw new COMException($"D3D11 Map hr=0x{hr:X8}", hr);

                    try
                    {
                        byte* src   = (byte*)mapped.pData;
                        uint  pitch = mapped.RowPitch;
                        int   srcW  = _contentWidth;
                        int   srcH  = _contentHeight;

                        // Build / refresh precomputed index tables when the source resolution
                        // changes. Saves ~14,400 long-multiply+divide ops per tick on the inner loop.
                        if (_srcXIndex is null || _srcYIndex is null
                            || _srcXIndex.Length != dstW || _srcYIndex.Length != dstH
                            || _indexedSrcW != srcW || _indexedSrcH != srcH)
                        {
                            _srcXIndex = new int[dstW];
                            _srcYIndex = new int[dstH];
                            for (int dx = 0; dx < dstW; dx++) _srcXIndex[dx] = (int)((long)dx * srcW / dstW) * 4; // BGRA byte offset
                            for (int dy = 0; dy < dstH; dy++) _srcYIndex[dy] = (int)((long)dy * srcH / dstH);
                            _indexedSrcW = srcW;
                            _indexedSrcH = srcH;
                        }

                        // Nearest-neighbour step-sample: full res -> 160x90 greyscale
                        for (int dstY = 0; dstY < dstH; dstY++)
                        {
                            byte* srcRow = src + ((long)_srcYIndex[dstY] * pitch);
                            int dstRowOffset = dstY * dstW;
                            for (int dstX = 0; dstX < dstW; dstX++)
                            {
                                int offset = _srcXIndex[dstX];
                                byte b = srcRow[offset];
                                byte g = srcRow[offset + 1];
                                byte r = srcRow[offset + 2];
                                // BT.601 integer greyscale: (0.114B + 0.587G + 0.299R) x 256
                                result[dstRowOffset + dstX] = (byte)((19 * b + 150 * g + 77 * r) >> 8);
                            }
                        }

                        // ── Pop-up sprint Task 1 (Option B): bar-region exclusion ───────
                        // Map the monitor-coords mask rect to downsample-grid cells and
                        // zero them in BOTH `result` (this frame) and `_prevFrame`
                        // (last frame). Zeroing only `result` would still produce
                        // |0 - prevValue| on the MAD pass — phantom motion. By zeroing
                        // both, the masked cells contribute 0 to the MAD sum permanently
                        // once the mask is engaged. One-tick artefact on the first frame
                        // the mask is set (prevFrame still has bar pixels) is acceptable;
                        // the spike detector requires sustained motion to fire.
                        if (_excludeRect is { } mask && _prevFrame is not null
                            && _prevFrame.Length == result.Length)
                        {
                            int dxMin = (int)((long)mask.Left * dstW / srcW);
                            int dxMax = (int)((long)(mask.Left + mask.Width - 1) * dstW / srcW);
                            int dyMin = (int)((long)mask.Top * dstH / srcH);
                            int dyMax = (int)((long)(mask.Top + mask.Height - 1) * dstH / srcH);
                            // Clamp to grid bounds (e.g. bar partially off-screen).
                            if (dxMin < 0) dxMin = 0;
                            if (dyMin < 0) dyMin = 0;
                            if (dxMax >= dstW) dxMax = dstW - 1;
                            if (dyMax >= dstH) dyMax = dstH - 1;
                            // NE14: never mutate the shared EmptyFrameSentinel. When
                            // _prevFrame aliases it (after a prior null-frame tick) the
                            // sentinel is already all-zero, so skipping the write is
                            // behaviour-preserving and protects the shared static.
                            bool prevIsSentinel = ReferenceEquals(_prevFrame, EmptyFrameSentinel);
                            for (int dy = dyMin; dy <= dyMax; dy++)
                            {
                                int row = dy * dstW;
                                for (int dx = dxMin; dx <= dxMax; dx++)
                                {
                                    result[row + dx] = 0;
                                    if (!prevIsSentinel) _prevFrame[row + dx] = 0;
                                }
                            }
                        }
                    }
                    finally
                    {
                        // Unmap(pResource, Subresource) — vtable[15]
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                            vtbl[VtblUnmap])(_ctxPtr, _stagingResPtr, 0);
                    }
                }
                finally
                {
                    // (releases moved to the outer finally so they also clean up the
                    //  pre-CopyResource failure paths above.)
                }
            }
        }
        finally
        {
            SafeRelease(ref gpuResPtr);
            try { if (texObj is not null && Marshal.IsComObject(texObj)) Marshal.ReleaseComObject(texObj); } catch { }
            // dxgiAccess is a legacy [ComImport] RCW (from .As<T>() on a [ComImport] interface);
            // ReleaseComObject decrements the RCW so the underlying +1 ref on the frame texture
            // is dropped this tick instead of waiting for GC.
            try { if (dxgiAccess is not null && Marshal.IsComObject(dxgiAccess)) Marshal.ReleaseComObject(dxgiAccess); } catch { }
            // The WinRT projected IDirect3DSurface is IDisposable in CsWinRT; disposing it
            // releases the projection's +1 ref on the same underlying surface.
            try { (surface as IDisposable)?.Dispose(); } catch { }
            frame.Dispose();
        }

        // Path Z bug-fix 2: only the full-success path reaches here without
        // hitting one of the early-return guards above. All early returns
        // leave realFrame=false so the watchdog correctly sees "no real
        // frame yet" and can re-init if the dry spell lasts past 2 s.
        realFrame = true;
        return result;
    }

    // Mean absolute difference per pixel (input range 0-255). Internal (not private)
    // so M1 WgcSensingProviderCalibrationTests can feed canned greyscale frames and
    // assert the MAD → OFI band mapping without a live capture session
    // (InternalsVisibleTo SenseEase.Tests). Pure + static — no instance state touched.
    internal static double ComputeMad(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    /// <summary>
    /// N-tap (configured via SenseDefaults.VimsMedianTaps) running median on raw OFI.
    /// Rejects 1–2 frame outliers (scene cuts, cursor flicks) before they corrupt the EMA.
    /// Until the buffer is full, returns the input.
    /// </summary>
    private double PushAndMedian(double sample)
    {
        _ofiHistory[_ofiHistoryHead] = sample;
        _ofiHistoryHead = (_ofiHistoryHead + 1) % _ofiHistory.Length;
        if (_ofiHistoryFilled < _ofiHistory.Length) _ofiHistoryFilled++;
        if (_ofiHistoryFilled < _ofiHistory.Length) return sample;

        Span<double> sorted = stackalloc double[_ofiHistory.Length];
        _ofiHistory.AsSpan().CopyTo(sorted);
        sorted.Sort();
        return sorted[_ofiHistory.Length / 2];
    }
}
