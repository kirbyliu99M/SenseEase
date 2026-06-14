using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.Infrastructure.Real;

/// <summary>FEAT-08 (CW-10): coarse head-tracking health for the opted-in user.
/// Derived from what <see cref="SidecarHeadPoseReader"/> already knows — process
/// exit, the no-frames watchdog, and recent parse activity. NO UI here; B1b
/// wires Settings + tray surfacing, mirroring the D3 Faulted pattern.</summary>
public enum HeadTrackingHealth
{
    /// <summary>Fresh head-pose samples are arriving within the staleness
    /// window — tracking is working.</summary>
    Healthy,

    /// <summary>The sidecar is notionally alive (started, not crashed) but no
    /// fresh sample has arrived recently (never produced one, or the stream
    /// stalled). The most common cause is AV hanging the unsigned exe at
    /// startup, an unavailable webcam, or a MediaPipe init failure.</summary>
    NoFrames,

    /// <summary>The sidecar process has exited / failed integrity / never
    /// started, or the reader is disposed. <see cref="SidecarHeadPoseReader.ReadLatest"/>
    /// returns null and the mask sits at center.</summary>
    Dead,
}

/// <summary>M4 (head-tracking sprint): IHeadPoseReader implementation that
/// spawns the Python sidecar (<c>tools/head-tracker-sidecar/head_tracker.py</c>)
/// as a child process and consumes one JSON line per frame from its stdout.
///
/// <para><b>Threading model.</b> The reader's parse loop runs on a long-lived
/// background <see cref="Task"/> (NOT a thread-pool timer). The "latest sample"
/// field is written from that background task and read from any thread via
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/>. <b>Consumers MUST marshal
/// to the WPF dispatcher</b> before touching any UI element with the returned
/// sample — the reader intentionally never marshals on its own.</para>
///
/// <para>This mirrors the v0.9.3 lesson learned with WgcSensingProvider →
/// OverlayWindow: a background pipeline that touched WPF dependency
/// properties directly caused intermittent dispatcher violations under load.
/// The fix in v0.9.3 was to keep the pipeline non-UI and have the consumer
/// wrap with <c>Dispatcher.BeginInvoke</c>. M5 (overlay wiring) MUST follow
/// the same pattern when it subscribes to this reader.</para>
///
/// <para><b>Sidecar-kill safety (OBS-2 hard requirement).</b> If the sidecar
/// dies (crash, taskkill, OOM, model-load failure), the C# host MUST NOT
/// crash. We subscribe to <see cref="Process.Exited"/> and flip an internal
/// dead flag; <see cref="ReadLatest"/> then returns null and consumers fall
/// back to the (0.5, 0.5) center default per <see cref="IHeadPoseReader"/>'s
/// contract. No auto-restart in M4 — that could mask repeated crashes; defer
/// to a future feature.</para>
///
/// <para><b>Staleness fallback.</b> If no fresh sample has arrived in the
/// last <see cref="StalenessThresholdMs"/> (= 2000 ms) the reader returns
/// null even if the sidecar is still notionally alive. This catches a frozen
/// sidecar that hasn't crashed but isn't emitting (model stall, capture
/// stuck).</para>
///
/// <para><b>Privacy invariant.</b> The sidecar's JSON line only carries
/// derived fields (cx, cy, luma, face_detected, face_present). No raw
/// landmarks, no frame data, no face image ever crosses the IPC boundary.
/// See <c>tools/head-tracker-sidecar/PRIVACY.md</c>.</para></summary>
public sealed class SidecarHeadPoseReader : IHeadPoseReader, IDisposable
{
    /// <summary>If the most recent sample is older than this, ReadLatest()
    /// returns null even if the sidecar process is still alive. Acts as a
    /// secondary safety net for the case where the sidecar wedges without
    /// crashing (model stall, webcam frozen, etc.).</summary>
    public const int StalenessThresholdMs = 2000;

    // Beta-1 #10 (clinical-safety): on face loss the sidecar keeps emitting the last-valid
    // cx/cy with FaceDetected=false (SIDE-04), so _latestStoredAtMs keeps refreshing and the
    // staleness check above ALWAYS passes — Health would report Healthy while no face is
    // actually tracked, masking a VIMS-protection gap on the tray / Settings indicator. Past
    // this grace with no FaceDetected==true sample, ComputeHealth reports NoFrames instead.
    // Matches the pump's FaceLossGraceMs so the health surface and the overlay agree.
    public const int FaceLossHealthGraceMs = 2000;

    // Pre-Alpha v0.11.1: replaces _pythonExePath + _sidecarScriptPath. The
    // PyInstaller --onedir bundle is a single self-contained exe -- no
    // separate interpreter to invoke.
    private readonly string _sidecarExePath;
    private readonly Action<string>? _log;
    private readonly bool _mirror;
    private readonly int? _webcamIndex;

    // Holds the most recent successfully parsed sample. Replaced via
    // Interlocked.Exchange from the reader task; read by any thread via
    // ReadLatest. Boxed-via-reference semantics make this safe without a lock.
    private HeadPoseSample? _latest;

    // Monotonic timestamp (UTC ticks) of when the latest sample was stored.
    // Used to enforce the staleness fallback. Read/written with Interlocked.
    private long _latestStoredAtMs; // NE10: monotonic Environment.TickCount64 (immune to wall-clock / NTP / VM-resume jumps)
    private long _latestFaceDetectedAtMs; // Beta-1 #10: monotonic TickCount64 of the last FaceDetected==true sample (0 = never)

    // Volatile so OnProcessExited (ThreadPool process-monitor thread) and
    // ReaderLoop's terminal block read a fresh reference for the identity
    // check — see fix #1 below. The _process field is mutated under
    // _lifecycleGate by DoStart/DoStop/Restart/Dispose, but the readers
    // are not gated (event-callback / abandoned-loop paths), so volatile
    // semantics matter.
    private volatile Process? _process;
    private Task?    _readerTask;
    private CancellationTokenSource? _cts;

    private int _sidecarDead;   // 0 = alive, 1 = dead. Interlocked-flag.
    private int _started;       // 0 = not started, 1 = started. Interlocked-flag.
    private int _disposed;      // 0 = live, 1 = disposed. Interlocked-flag.

    // FEAT-08 (CW-10): last health value PUBLISHED via HealthChanged. Stored as
    // the underlying int so it can be swapped with Interlocked. Seeded to Dead
    // (the pre-Start state) so the first transition to Healthy/NoFrames fires.
    private int _lastPublishedHealth = (int)HeadTrackingHealth.Dead;

    // UI Consolidation Sprint Phase 3 fix (2026-05-30, adversarial review):
    // serialization gate across Stop / Start / Restart / Dispose. Without
    // this, rapid Power Save toggling via MainWindow.ApplyPowerSave's
    // fire-and-forget Task.Run calls could (a) spawn an orphan sidecar
    // while UI shows Power Save ON (the D-B webcam-release privacy proof
    // breaks silently) or (b) leave the freshly-restarted sidecar dead
    // because a stale Stop wins the race against the new Start.
    // SemaphoreSlim(1,1) gives a simple non-reentrant mutex.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // H38: bound the lifecycle-gate waits so a wedged Start/Stop/Restart can't hang
    // the caller (and shutdown) forever; shorter bound on the Dispose path.
    private const int GateWaitTimeoutMs        = 10_000;
    private const int GateWaitTimeoutDisposeMs = 2_000;

    // Small ring of stderr tail for diagnostics when the sidecar exits.
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderrTail = new();
    private const int StderrTailMax = 4096;

    // M5.1-A Layer 3 was REMOVED in M5.1-A v2 (2026-05-29). The temporal
    // median filter blocked fast intentional travel to screen corners — when
    // user push their head rapidly toward an edge, the median lagged behind,
    // and each new sample was substituted with the stale median for 2-3
    // frames. User reported "unable to push to corner" during M5.1-A UVHS.
    // Diagnose CSV showed zero outliers in 22s of varied motion, so Layer 3
    // was prophylactic for a problem that didn't exist while creating a real
    // problem. Layer 1 (sidecar 25° jump filter) is sufficient for glitch
    // protection. If outliers later appear (e.g., M5.1-C perf changes,
    // future sidecar tuning), re-introduce with velocity-aware thresholding
    // rather than positional median.

    // M5.1-C PRECONDITION instrumentation (2026-05-29 per advisor sign-off):
    // count successful JSON-line parses so the C# side can directly measure
    // sidecar received Hz under full-active load. The webcam can only be held
    // by one process at a time, so we can't run sidecar --soak alongside the
    // dashboard. This counter is the alternative: dashboard runs normally,
    // sidecar produces samples that the reader receives + parses + counts.
    // MainWindow's 5-second timer-rate log reads + resets the counter so the
    // same log line carries both achieved-timer-Hz and received-sidecar-Hz.
    // Interlocked because incremented on the reader Task, read on the UI
    // dispatcher thread.
    private int _receivedSampleCount;

    // Startup no-frame watchdog. If the spawned sidecar produces ZERO head-pose frames
    // within StartupNoFrameWatchdogMs, log a clear diagnostic. The dominant real-world
    // cause (observed on Kirby's machine 2026-06-07) is antivirus AMSI / behavioral
    // scanning HANGING the UNSIGNED head_tracker.exe at native-import time: the process
    // spawns (pid logged) but never reaches its emit loop, so ReadLatest stays null and
    // the FOV mask sits pinned at center with NO other signal. Before this watchdog the
    // failure was completely silent. One-shot Timer; ~8 s is well above the healthy
    // ~1-2 s first-frame warm-up so it never false-positives on a slow-but-working start.
    private System.Threading.Timer? _startupWatchdog;
    private const int StartupNoFrameWatchdogMs = 8000;

    /// <summary>M5.1-C PRECONDITION: atomically read + reset the
    /// received-sample counter. Returns the count of successful parses since
    /// the last call (or since reader start). Safe to call from any thread.</summary>
    public int ConsumeReceivedSampleCount()
    {
        return Interlocked.Exchange(ref _receivedSampleCount, 0);
    }

    /// <summary>FEAT-08 (CW-10): raised when the derived head-tracking health
    /// transitions (e.g. NoFrames → Healthy on the first frame, Healthy → Dead
    /// on sidecar exit). Always raised from a BACKGROUND thread (reader Task,
    /// process-monitor thread, watchdog timer, or a poller's thread) — a
    /// subscriber touching UI MUST marshal to the dispatcher, exactly like
    /// <see cref="ReadLatest"/>'s consumer contract. B1b wires this to Settings
    /// + tray; no UI is consumed here.</summary>
    public event EventHandler<HeadTrackingHealth>? HealthChanged;

    /// <summary>FEAT-08 (CW-10): current coarse head-tracking health, derived
    /// live from process / watchdog / parse state. Polling is cheap (a few
    /// Volatile/Interlocked reads). Does NOT raise <see cref="HealthChanged"/>
    /// — call <see cref="RefreshHealth"/> for the event-raising path.
    ///
    /// <list type="bullet">
    ///   <item><see cref="HeadTrackingHealth.Dead"/> — disposed, not started,
    ///         or the sidecar flagged dead (exit / integrity / spawn fail).</item>
    ///   <item><see cref="HeadTrackingHealth.NoFrames"/> — alive but no fresh
    ///         sample within <see cref="StalenessThresholdMs"/> (never produced
    ///         one, or the stream stalled).</item>
    ///   <item><see cref="HeadTrackingHealth.Healthy"/> — a sample arrived
    ///         within the staleness window.</item>
    /// </list></summary>
    public HeadTrackingHealth Health => ComputeHealth();

    private HeadTrackingHealth ComputeHealth()
    {
        if (Volatile.Read(ref _disposed) == 1)   return HeadTrackingHealth.Dead;
        if (Volatile.Read(ref _started)  == 0)   return HeadTrackingHealth.Dead;
        if (Volatile.Read(ref _sidecarDead) == 1) return HeadTrackingHealth.Dead;

        long storedAtMs = Interlocked.Read(ref _latestStoredAtMs);
        if (storedAtMs == 0) return HeadTrackingHealth.NoFrames; // never received a frame
        long ageMs = Environment.TickCount64 - storedAtMs;       // NE10: monotonic
        if (ageMs > StalenessThresholdMs) return HeadTrackingHealth.NoFrames;

        // Beta-1 #10: a fresh sample is not enough — the sidecar streams FaceDetected=false
        // frames on face loss. Report NoFrames once no face has been seen for longer than the
        // grace, so the health surface stops claiming tracking works while no face is present.
        long faceAtMs = Interlocked.Read(ref _latestFaceDetectedAtMs);
        if (faceAtMs == 0) return HeadTrackingHealth.NoFrames; // alive but never tracked a face
        if (Environment.TickCount64 - faceAtMs > FaceLossHealthGraceMs) return HeadTrackingHealth.NoFrames;
        return HeadTrackingHealth.Healthy;
    }

    /// <summary>FEAT-08 (CW-10): recompute <see cref="Health"/> and raise
    /// <see cref="HealthChanged"/> if it transitioned since the last publish.
    /// Called at every state edge the reader already observes (sample stored,
    /// sidecar exit, spawn failure, stop/restart). B1b's poller / watchdog may
    /// also call this to catch the time-based Healthy → NoFrames staleness
    /// edge (which has no underlying event). Idempotent + thread-safe: the
    /// compare-exchange ensures exactly one subscriber notification per
    /// distinct transition even under concurrent callers. Never throws —
    /// a subscriber exception must not destabilise the reader.</summary>
    public void RefreshHealth()
    {
        var current = ComputeHealth();
        int prev = Interlocked.Exchange(ref _lastPublishedHealth, (int)current);
        if (prev == (int)current) return; // no transition — stay quiet
        try { HealthChanged?.Invoke(this, current); }
        catch { /* a health subscriber must never destabilise the reader */ }
    }

    // H35: upper bound on a single stdout line. The bounded reader gives up and
    // drops the partial once this many chars accumulate without a newline, so a
    // hostile/wedged sidecar emitting one unbounded line can't OOM the host.
    // Legit lines are ~150 bytes, so 64 KB is a >400x safety margin.
    private const int MaxLineChars = 64 * 1024;

    // H36: rate-limit the malformed-line diagnostic with exponential backoff so
    // a wedged sidecar emitting bad lines at frame rate can't flood the diag
    // log. We log the 1st malformed line, then the 2nd, 4th, 8th ... up to a
    // cap of one-per-MalformedLogMaxInterval. The counter resets on the next
    // successful parse. All of this state lives on the single reader Task, so
    // no synchronization is needed beyond the Interlocked total counter.
    private const int MalformedLogMaxInterval = 256; // log at most 1 / 256 bad lines once saturated
    private int _malformedTotal;                      // total malformed lines (diagnostics, Interlocked)

    /// <summary>H36: atomically read + reset the count of malformed sidecar
    /// lines dropped since the last call (or since reader start). Lets a
    /// diagnostics consumer surface a flood that the rate-limited log
    /// intentionally suppresses. Safe to call from any thread.</summary>
    public int ConsumeDroppedMalformedCount()
    {
        return Interlocked.Exchange(ref _malformedTotal, 0);
    }

    /// <summary>Construct the reader. Does NOT spawn the sidecar — call
    /// <see cref="Start"/> for that. Separating construction from startup
    /// keeps the flag-off path zero-cost: if HeadTrackingEnabled is false,
    /// nothing instantiates this class at all.</summary>
    /// <param name="sidecarExePath">Absolute path to the frozen sidecar exe
    /// (the PyInstaller --onedir bundle's self-contained entry point — no
    /// separate Python interpreter or script). See <see cref="_sidecarExePath"/>.</param>
    /// <param name="log">Optional callback invoked with diagnostic messages
    /// (sidecar startup, exit, parse warnings). Always called from a
    /// background thread — callers must marshal if they touch UI.</param>
    /// <param name="mirror">Maps to the sidecar's --mirror / --no-mirror
    /// flag. Default true (selfie-view) per G1 advisor Q4.</param>
    /// <param name="webcamIndex">Webcam index (0 = default device). The
    /// sidecar also accepts a video file path via its --input arg, but the
    /// C# host only spawns webcam-input sidecars in production.</param>
    public SidecarHeadPoseReader(
        string sidecarExePath,
        Action<string>? log = null,
        bool mirror = true,
        int? webcamIndex = 0)
    {
        _sidecarExePath = sidecarExePath ?? throw new ArgumentNullException(nameof(sidecarExePath));
        _log            = log;
        _mirror         = mirror;
        _webcamIndex    = webcamIndex;
    }

    /// <summary>Spawn the sidecar process and start the background reader
    /// task. Idempotent: a second call is a no-op. Best-effort: if the
    /// Python binary or script isn't where we expect, log + flip the dead
    /// flag so ReadLatest() returns null and the consumer falls back to
    /// center default. The app does NOT crash on a missing sidecar.
    ///
    /// <para>Phase 3 fix (2026-05-30): public surface acquires the
    /// <see cref="_lifecycleGate"/> so concurrent Start/Stop/Restart from
    /// Power Save Task.Run callers are serialized. The pre-gate disposed
    /// check is a fast-path; a re-check inside the gate guards against
    /// disposal racing past the fast-path.</para></summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        if (!_lifecycleGate.Wait(GateWaitTimeoutMs)) { _log?.Invoke("[SidecarHeadPoseReader] lifecycle-gate wait timed out — aborting Start."); return; } // H38
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            DoStart();
        }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>Gate-internal Start body. CALLER MUST hold _lifecycleGate.
    /// Used by public Start (acquires gate) and Restart (already holds gate).
    /// The _started one-shot guard is retained inside the gate so a second
    /// DoStart inside the same gated transaction is still a no-op — defensive
    /// against future callers.</summary>
    private void DoStart()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        try
        {
            if (!File.Exists(_sidecarExePath))
            {
                // SEC-01: redact the user-profile segment of the full exe path
                // before it reaches wgc_diag.txt / the handbook log-zip.
                _log?.Invoke(PathRedactor.Redact($"[SidecarHeadPoseReader] Frozen sidecar exe not found at '{_sidecarExePath}'. Sidecar disabled; ReadLatest will return null."));
                Interlocked.Exchange(ref _sidecarDead, 1);
                RefreshHealth(); // FEAT-08: spawn precondition failed → Dead
                return;
            }

            // H32: per-subprocess integrity gate. Verify the sidecar exe against the
            // install manifest before spawning. Absent manifest (dev/alpha builds) is
            // a no-op pass; a tampered/replaced head_tracker.exe on a packaged build
            // fails closed here. Degrade gracefully — mark the sidecar dead and return
            // so ReadLatest() yields null and the consumer falls back to the (0.5,0.5)
            // center default. No crash (OBS-2 hard requirement).
            // Verify the FULL resolved path (not a bare "head_tracker.exe", which
            // IntegrityVerifier would resolve next to BaseDirectory and miss — the
            // sidecar ships nested). Keyed in the manifest by Path.GetFileName.
            if (!IntegrityVerifier.Verify(_sidecarExePath))
            {
                _log?.Invoke("[SidecarHeadPoseReader] head_tracker.exe failed integrity verification. Sidecar disabled; ReadLatest will return null.");
                Interlocked.Exchange(ref _sidecarDead, 1);
                RefreshHealth(); // FEAT-08: integrity gate failed → Dead
                return;
            }

            string args = BuildSidecarArgs(_webcamIndex, _mirror);
            var psi = new ProcessStartInfo
            {
                FileName               = _sidecarExePath,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // Run the sidecar in its own folder so relative model
                // lookups (./face_landmarker.task) and the runtime-hook's
                // exe-adjacent probe both resolve correctly even when the
                // host EXE lives elsewhere (e.g. a published install).
                WorkingDirectory       = Path.GetDirectoryName(_sidecarExePath) ?? string.Empty,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            // H12: strip inherited env vars that could hijack the model load or
            // corrupt the JSON stdout stream. A non-privileged HKCU\Environment
            // / setx can set SENSEEASE_FACE_LANDMARKER_MODEL to redirect the
            // sidecar's model load to an attacker path; the PYTHON* vars can
            // change stdout encoding/buffering and break line framing. Remove()
            // is a no-op when the var is absent, so this is always safe.
            psi.EnvironmentVariables.Remove("SENSEEASE_FACE_LANDMARKER_MODEL");
            psi.EnvironmentVariables.Remove("PYTHONIOENCODING");
            psi.EnvironmentVariables.Remove("PYTHONUTF8");
            psi.EnvironmentVariables.Remove("PYTHONLEGACYWINDOWSSTDIO");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += OnProcessExited;
            proc.ErrorDataReceived += OnStderrLine;
            // H37: publish _process BEFORE Start() so a fast-exit Exited event
            // (which can fire as soon as the process starts) sees the handle and
            // logs the exit correctly instead of against a null _process.
            _process = proc;

            if (!proc.Start())
            {
                _log?.Invoke(PathRedactor.Redact($"[SidecarHeadPoseReader] Process.Start returned false for '{_sidecarExePath} {args}'. Sidecar disabled.")); // SEC-01
                Interlocked.Exchange(ref _sidecarDead, 1);
                _process = null;
                RefreshHealth(); // FEAT-08: spawn returned false → Dead
                return;
            }
            proc.BeginErrorReadLine();

            // Reap the sidecar via the existing Windows JobObject infra so it
            // dies with us even on a hard host crash. Same pattern used for
            // llama-server. Best-effort — failure here is non-fatal.
            // SIDE-05: log any Win32/ctor failure captured in LastError.
            try { ChildProcessReaper.Adopt(proc); } catch { }
            if (ChildProcessReaper.LastError is { } reaperErr)
                _log?.Invoke(reaperErr);

            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _readerTask = Task.Factory.StartNew(
                () => ReaderLoop(proc, ct),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _log?.Invoke(PathRedactor.Redact($"[SidecarHeadPoseReader] Started pid={proc.Id} '{_sidecarExePath} {args}'")); // SEC-01 (the line named in the finding)
            RefreshHealth(); // FEAT-08: started + spawn ok → NoFrames (until first frame flips Healthy)
            ArmStartupWatchdog(proc);
        }
        catch (Exception ex)
        {
            _log?.Invoke(PathRedactor.Redact($"[SidecarHeadPoseReader] Start failed: {ex.GetType().Name}: {ex.Message}. Sidecar disabled.")); // SEC-01: ex.Message may embed paths
            Interlocked.Exchange(ref _sidecarDead, 1);
            RefreshHealth(); // FEAT-08: spawn threw → Dead
        }
    }

    /// <summary>Arm a one-shot timer that fires <see cref="StartupNoFrameWatchdogMs"/> after a
    /// successful spawn and, if NO head-pose frame has been received by then (and the process is
    /// still this generation + alive + not already torn down), logs a clear, actionable
    /// diagnostic. This converts the previously-silent "sidecar spawns but emits nothing" failure
    /// (most often antivirus AMSI/behavioral scanning hanging the unsigned exe at native-import,
    /// but also webcam-unavailable or a MediaPipe init failure) into a self-explaining log line.
    /// Best-effort: any failure inside the callback is swallowed — a diagnostic must never crash
    /// the reader.</summary>
    private void ArmStartupWatchdog(Process proc)
    {
        int pid;
        try { pid = proc.Id; } catch { return; }

        try { _startupWatchdog?.Dispose(); } catch { }
        _startupWatchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 1) return;
                if (Interlocked.Read(ref _latestStoredAtMs) != 0) return; // frames ARE flowing — healthy, stay quiet
                if (!ReferenceEquals(_process, proc)) return;             // superseded by a Restart generation
                bool alive;
                try { alive = !proc.HasExited; } catch { alive = false; }
                if (!alive) return;                                       // an actual exit is logged by OnProcessExited
                // FEAT-08: spawned-but-silent → NoFrames transition (B1b surfaces
                // it). Health is also independently pollable by B1b's timer.
                RefreshHealth();
                _log?.Invoke(
                    $"[SidecarHeadPoseReader] WATCHDOG: sidecar pid={pid} spawned but produced 0 head-pose " +
                    $"frames in {StartupNoFrameWatchdogMs / 1000}s — head-tracking will stay INACTIVE (the FOV " +
                    "mask stays centered). Most likely the UNSIGNED sidecar is being hung at startup by " +
                    "antivirus AMSI/behavioral scanning (add head_tracker.exe to your AV's trusted-program / " +
                    "exception list, not just a folder file-scan exclusion), or the webcam is unavailable, or " +
                    "MediaPipe failed to initialize. Confirm by running 'head_tracker.exe --input 0 --debug' " +
                    "from a terminal — healthy output is one JSON line per ~66ms.");
            }
            catch { /* watchdog is a best-effort diagnostic */ }
        }, null, StartupNoFrameWatchdogMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>Returns the most recent head-pose sample, or null if:
    /// <list type="bullet">
    ///   <item>The sidecar has not been started yet.</item>
    ///   <item>The sidecar has exited (crash, kill, OOM, etc).</item>
    ///   <item>No fresh sample has arrived in the last 2 seconds
    ///         (staleness fallback for a wedged sidecar).</item>
    /// </list>
    /// Null is the "use (0.5, 0.5) center default" signal per
    /// <see cref="IHeadPoseReader"/>'s contract.
    ///
    /// <para>Safe to call from any thread (UI or background). The returned
    /// <see cref="HeadPoseSample"/> is immutable (a record).</para></summary>
    public HeadPoseSample? ReadLatest()
    {
        if (Volatile.Read(ref _disposed) == 1) return null;
        if (Volatile.Read(ref _started) == 0) return null;
        if (Volatile.Read(ref _sidecarDead) == 1) return null;

        var sample = Volatile.Read(ref _latest);
        if (sample is null) return null;

        long storedAtMs = Interlocked.Read(ref _latestStoredAtMs);
        long ageMs      = Environment.TickCount64 - storedAtMs; // NE10: monotonic
        if (ageMs > StalenessThresholdMs) return null;

        return sample;
    }

    /// <summary>Stop the sidecar and wait for the reader task to finish.
    /// Idempotent. Sets the dead flag so any in-flight ReadLatest call
    /// returns null even before the process actually exits.
    ///
    /// <para>Phase 3 fix (2026-05-30): public surface acquires
    /// <see cref="_lifecycleGate"/> for serialization across concurrent
    /// Power Save Task.Run callers.</para>
    ///
    /// <para>Phase 3 fix-round-2 (2026-05-30, verification HIGH #C): added
    /// pre-gate <c>_disposed</c> fast-path matching <see cref="Start"/> and
    /// <see cref="Restart"/>. Without this, a Task.Run-queued Stop that
    /// fires AFTER <see cref="Dispose"/> ran would hit
    /// <see cref="ObjectDisposedException"/> from <see cref="SemaphoreSlim.Wait()"/>
    /// on the disposed gate. The Phase 3 caller (MainWindow.ApplyPowerSave)
    /// wraps in catch but that empty catch directly undermines BLOCKER-3's
    /// diagnostic-visibility goal at the most likely failure path
    /// (app shutdown).</para></summary>
    public void Stop()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        if (!_lifecycleGate.Wait(GateWaitTimeoutMs)) { _log?.Invoke("[SidecarHeadPoseReader] lifecycle-gate wait timed out — aborting Stop."); return; } // H38
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            DoStop();
        }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>Gate-internal Stop body. CALLER MUST hold _lifecycleGate.
    ///
    /// <para>Phase 3 fix (2026-05-30): unsubscribes <c>OnProcessExited</c>
    /// and <c>OnStderrLine</c> from the live Process BEFORE Kill so that
    /// a late-firing Exited callback for THIS generation cannot land on a
    /// future generation's _process / _sidecarDead state. The unsubscribe
    /// alone is racy (the callback may already be queued by .NET's process
    /// monitor before -= runs), so <see cref="OnProcessExited"/> ALSO does
    /// an identity check via <c>ReferenceEquals(sender, _process)</c>.</para></summary>
    private void DoStop()
    {
        if (Volatile.Read(ref _started) == 0) return;

        Interlocked.Exchange(ref _sidecarDead, 1);
        RefreshHealth(); // FEAT-08: caller-initiated stop → Dead transition
        try { _startupWatchdog?.Dispose(); _startupWatchdog = null; } catch { }
        try { _cts?.Cancel(); } catch { }

        var proc = _process;
        int pidForLog = -1;
        if (proc is not null)
        {
            // Snapshot the pid BEFORE Kill so we can log it even if the
            // Process handle goes into an unusable state mid-tear-down.
            try { pidForLog = proc.Id; } catch { }

            // Phase 3 fix #2: unsubscribe handlers before Kill. Belt; the
            // identity check in OnProcessExited is the suspenders.
            try { proc.Exited            -= OnProcessExited; } catch { }
            try { proc.ErrorDataReceived -= OnStderrLine;    } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    // SIGTERM equivalent on Windows: Kill(entireProcessTree:false)
                    // sends a Terminate. The JobObject reaper would also catch it
                    // on host exit, but explicit kill is faster here.
                    proc.Kill(entireProcessTree: false);
                }
            }
            catch { /* best effort */ }

            // Phase 3 UVHS audit fix (2026-05-30): emit caller-initiated
            // stop diagnostic. Round-1 Fix #1 (proc.Exited -= above)
            // intentionally prevents OnProcessExited from running on the
            // common Stop/Restart path — which silenced the "Sidecar exited
            // (code=...)" log line at OnProcessExited too. The UVHS audit
            // workflow confirmed 7 Started pids + 0 exit lines in a healthy
            // 6-restart-cycle run. This 1-line patch restores diagnostic
            // parity with the spawn-side "Started pid=N" log. Distinct
            // phrasing ("Sidecar stopped, caller-initiated") so the original
            // OnProcessExited log line ("Sidecar exited (code=X). stderr
            // tail: ...") now means UNEXPECTED death — a strictly more
            // useful signal that should be ZERO during a clean UVHS run.
            _log?.Invoke(FormatCallerInitiatedStopLog(pidForLog));
        }

        try { _readerTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Tear down the startup watchdog unconditionally up front — the gate-timeout
        // branch below skips DoStop, and the callback already no-ops once _disposed=1,
        // but disposing here releases the timer promptly regardless of the gate path.
        try { _startupWatchdog?.Dispose(); _startupWatchdog = null; } catch { }

        // Phase 3 fix (2026-05-30): acquire the lifecycle gate so any
        // in-flight Power-Save-triggered Stop/Restart finishes before we
        // tear down. Then call DoStop (not public Stop — we're already
        // inside the gate). Finally dispose the gate AFTER releasing it.
        try
        {
            // H38: bound the gate wait (2 s on the Dispose path) so a wedged
            // Start/Stop can't hang shutdown forever; on timeout, force on to
            // disposing the gate below without DoStop.
            if (_lifecycleGate.Wait(GateWaitTimeoutDisposeMs))
            {
                try
                {
                    DoStop();
                    try { _process?.Dispose(); } catch { }
                    try { _cts?.Dispose(); }     catch { }
                    _process    = null;
                    _readerTask = null;
                    _cts        = null;
                }
                finally { _lifecycleGate.Release(); }
            }
            else
            {
                // Beta-1 #9: a wedged in-flight Start/Stop/Restart held the gate past the 2 s
                // wait, so DoStop never runs and proc.Kill() is skipped. If the JobObject
                // backstop ALSO failed to arm, the child head_tracker.exe is orphaned (it holds
                // the webcam). Do NOT depend solely on the JobObject — attempt a direct
                // best-effort kill of the captured process outside the gate. Snapshot the field
                // so a concurrent null does not throw.
                _log?.Invoke("[SidecarHeadPoseReader] lifecycle-gate wait timed out (2s) — forcing Dispose without DoStop; best-effort killing the child directly.");
                var proc = _process;
                if (proc is not null)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: false); } catch { /* best-effort */ }
                    try { proc.Dispose(); } catch { }
                }
            }
        }
        catch { /* never throw from Dispose */ }

        try { _lifecycleGate.Dispose(); } catch { }
    }

    /// <summary>UI Consolidation Sprint Phase 3 (2026-05-30): stop the
    /// current sidecar (if running) and spawn a fresh one. Used by
    /// <c>MainWindow.ApplyPowerSave(false)</c> to bring head-tracking back
    /// after a Power Save session.
    ///
    /// <para>Behaviour: under <see cref="_lifecycleGate"/>, calls
    /// <see cref="DoStop"/> (which unsubscribes Exited + waits for the
    /// reader task to drain) + disposes the previous Process handle +
    /// resets the <c>_started</c> / <c>_sidecarDead</c> one-shot flags +
    /// clears the stale <c>_latest</c> sample + calls <see cref="DoStart"/>.
    /// After this returns, the new sidecar takes ~1-2 s to produce the first
    /// frame (MediaPipe model warm-up + webcam initialisation); during the
    /// warm-up <see cref="ReadLatest"/> returns null and the FOV mask sits
    /// at (0.5, 0.5) center default per <see cref="IHeadPoseReader"/>'s
    /// contract — the same path used when the sidecar dies, so no new UX
    /// shimmer.</para>
    ///
    /// <para>Phase 3 review fix (2026-05-30): all lifecycle transitions are
    /// serialized through <see cref="_lifecycleGate"/>. A rapid Power Save
    /// PS-ON→PS-OFF→PS-ON sequence that posts multiple Task.Run callbacks
    /// will now execute them strictly serially — eliminating the
    /// "stale-Stop-after-Restart kills the new sidecar" and
    /// "two concurrent Restarts spawn two sidecars" races identified in
    /// the adversarial review. The non-atomic Stop+reset+Start sequence
    /// is now an atomic critical section.</para>
    ///
    /// <para>No-op if already disposed. Best-effort: spawn failure flips
    /// <c>_sidecarDead</c> back to 1 and the consumer sees null + falls
    /// back to center default (same as a startup failure).</para>
    ///
    /// <para>Thread-safety: callers should invoke from a background thread
    /// (e.g. <c>Task.Run</c>) because <see cref="DoStop"/> may block up to
    /// 3 seconds waiting for the reader task to drain and the gate may
    /// add additional queueing if multiple lifecycle ops are in flight.</para></summary>
    public void Restart()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        if (!_lifecycleGate.Wait(GateWaitTimeoutMs)) { _log?.Invoke("[SidecarHeadPoseReader] lifecycle-gate wait timed out — aborting Restart."); return; } // H38
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            DoStop();

            // Dispose the previous Process + CTS handles explicitly so we
            // don't leak OS handles across restart cycles. DoStart will
            // overwrite the fields with fresh instances.
            try { _process?.Dispose(); } catch { /* best-effort */ }
            try { _cts?.Dispose(); }     catch { /* best-effort */ }
            _process    = null;
            _readerTask = null;
            _cts        = null;

            // Reset the one-shot guards so DoStart can fire again. _disposed
            // is intentionally NOT reset — a disposed reader stays disposed.
            Interlocked.Exchange(ref _started, 0);
            Interlocked.Exchange(ref _sidecarDead, 0);
            Volatile.Write(ref _latest, null);
            Interlocked.Exchange(ref _latestStoredAtMs, 0);
            Interlocked.Exchange(ref _receivedSampleCount, 0);

            DoStart();
        }
        finally { _lifecycleGate.Release(); }
    }

    // ── Internals ──────────────────────────────────────────────────────────

    /// <summary>Phase 4 (UI Consolidation Sprint, 2026-05-30): exact log
    /// line emitted by <see cref="DoStop"/> when the caller (Stop / Restart
    /// / Dispose) initiates the sidecar tear-down. Extracted so a CI unit
    /// test can pin the diagnostic contract.
    ///
    /// <para>The phrase <c>"caller-initiated"</c> is load-bearing: it
    /// distinguishes a clean Stop from the <see cref="OnProcessExited"/>
    /// log line emitted on UNEXPECTED death (which should be ZERO during
    /// a healthy UVHS run). Renaming or dropping that word breaks the
    /// UVHS audit semantic used to validate Phase 3 Power-Save round-trip
    /// integrity. See the comment at the call site in <see cref="DoStop"/>
    /// for the full rationale.</para></summary>
    internal static string FormatCallerInitiatedStopLog(int pid)
        => $"[SidecarHeadPoseReader] Sidecar stopped (caller-initiated, pid={pid}).";

    /// <summary>Build the CLI argument string for the sidecar. Extracted so
    /// tests can verify argument shape without spawning a real process.</summary>
    internal static string BuildSidecarArgs(int? webcamIndex, bool mirror)
    {
        // Pre-Alpha v0.11.1: the frozen exe IS the entry point — no script
        // arg needed. Mirror flag uses BooleanOptionalAction on the sidecar
        // side (Q4): default ON, --no-mirror disables. Only emit the explicit
        // flag when the C# host wants to override the default.
        string input = (webcamIndex ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string mirrorFlag = mirror ? string.Empty : " --no-mirror";
        return $"--input {input}{mirrorFlag}";
    }

    private void ReaderLoop(Process proc, CancellationToken ct)
    {
        StreamReader stdout = proc.StandardOutput;

        // H35: hand-rolled bounded line accumulator (replaces StreamReader.
        // ReadLine, which has no length cap). Reused across iterations so we
        // don't reallocate per frame.
        var lineBuf = new StringBuilder(256);

        // H36: per-loop exponential-backoff state for the malformed-line diag.
        // Local (not field) so each ReaderLoop generation starts fresh. We log
        // the 1st bad line, then back off doubling the gap (1, 2, 4, ...) up to
        // a once-per-MalformedLogMaxInterval ceiling. Reset on a good parse.
        long malformedRun     = 0;   // consecutive malformed lines since last good parse
        long nextMalformedLog = 1;   // log when malformedRun reaches this threshold
        long logInterval      = 1;   // current backoff gap (doubles each emitted log)

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Phase 3 fix-round-2 (2026-05-30, verification HIGH #A):
                // identity check at every iteration. DoStop's _readerTask.
                // Wait(3s) can time out if stdout teardown is slow on the
                // kernel side; this loop may keep parsing buffered stdout
                // lines AFTER Restart has advanced _process to the new
                // generation. Without this guard, the abandoned reader's
                // next successful parse would silently inject a stale
                // sample into the NEW generation's _latest field, fooling
                // the 2-second staleness fallback for up to ~150ms of the
                // 165Hz CompositionRendering pull. The terminal _sidecarDead
                // write at line 502 has its own identity check; this
                // per-frame guard prevents the data-level write from
                // sneaking through first.
                if (!ReferenceEquals(proc, _process)) break;

                int outcome;
                string line;
                try
                {
                    outcome = ReadBoundedLine(stdout, lineBuf, out line);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[SidecarHeadPoseReader] stdout read threw {ex.GetType().Name}: {ex.Message}. Reader exiting.");
                    break;
                }

                if (outcome == ReadLineEof) break;        // EOF — sidecar closed stdout.
                if (outcome == ReadLineOverflow)
                {
                    // H35: a single line exceeded MaxLineChars without a newline.
                    // The partial was dropped (and the rest of the line is being
                    // discarded by ReadBoundedLine up to the next newline/EOF).
                    // Treat as a malformed line for rate-limit accounting so a
                    // flood of giant lines can't slip past the H36 throttle.
                    if (NoteMalformedLine(ref malformedRun, ref nextMalformedLog, ref logInterval))
                        _log?.Invoke($"[SidecarHeadPoseReader] Dropped over-length line (>{MaxLineChars} chars, malformed run={malformedRun}).");
                    continue;
                }
                if (line.Length == 0) continue;           // skip empty lines defensively.

                if (TryParseSampleLine(line, out HeadPoseSample? sample) && sample is not null)
                {
                    bool wasFirstOrStale = Interlocked.Read(ref _latestStoredAtMs) == 0;
                    Volatile.Write(ref _latest, sample);
                    Interlocked.Exchange(ref _latestStoredAtMs, Environment.TickCount64);
                    Interlocked.Increment(ref _receivedSampleCount); // M5.1-C
                    // Beta-1 #10: track FaceDetected freshness separately from stream freshness,
                    // and treat a face (re)acquisition as a NoFrames → Healthy edge.
                    bool faceRecovered = false;
                    if (sample.FaceDetected)
                    {
                        long prevFaceMs = Interlocked.Exchange(ref _latestFaceDetectedAtMs, Environment.TickCount64);
                        faceRecovered = prevFaceMs == 0
                            || (Environment.TickCount64 - prevFaceMs) > FaceLossHealthGraceMs;
                    }
                    // FEAT-08: first frame (or recovery from a stale gap) is a
                    // NoFrames/Dead → Healthy transition. RefreshHealth no-ops
                    // when already Healthy, so the per-frame call is cheap; only
                    // raise the event on the actual edge to avoid event spam.
                    if (wasFirstOrStale || faceRecovered) RefreshHealth();
                    // H36: a good parse resets the malformed backoff so the next
                    // bad line is logged immediately again.
                    malformedRun     = 0;
                    nextMalformedLog = 1;
                    logInterval      = 1;
                }
                else
                {
                    // Malformed line: keep the previous _latest so a single bad
                    // line doesn't trip the staleness fallback. This is the
                    // "fuzz-resilient" behavior asserted by SidecarHeadPoseReader
                    // _ParsesMalformedLine_DoesNotThrow.
                    //
                    // H36: rate-limit the diagnostic with exponential backoff so
                    // a wedged sidecar emitting bad lines at frame rate can't
                    // flood the diag log / saturate the reader on disk I/O.
                    if (NoteMalformedLine(ref malformedRun, ref nextMalformedLog, ref logInterval))
                        _log?.Invoke($"[SidecarHeadPoseReader] Skipped malformed line (len={line.Length}, malformed run={malformedRun}).");
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[SidecarHeadPoseReader] Reader loop unexpected exception {ex.GetType().Name}: {ex.Message}");
        }
        // Reader loop exiting = sidecar's stdout closed = effectively dead
        // even if Process.Exited hasn't fired yet. Flip the flag now so
        // ReadLatest returns null immediately.
        //
        // Phase 3 fix (2026-05-30, adversarial review blocker #1/#2 non-blocker):
        // identity check against the current _process. If Stop's 3-second
        // Wait timed out before this loop drained, Restart may have already
        // advanced to the next generation. An abandoned reader writing
        // _sidecarDead=1 onto the new generation would silently brick
        // head-tracking. Mirror the OnProcessExited defense.
        if (ReferenceEquals(proc, _process))
        {
            Interlocked.Exchange(ref _sidecarDead, 1);
            RefreshHealth(); // FEAT-08: stdout closed → Dead transition
        }
    }

    // H35: bounded-line read outcomes. A successful read returns the char
    // count of the line (>= 0) via the `line` out-param length; these two
    // sentinels distinguish EOF and over-length-drop from a normal read.
    private const int ReadLineEof      = -1;
    private const int ReadLineOk       = 0;
    private const int ReadLineOverflow = 1;

    /// <summary>H35: read one newline-terminated line from <paramref name="reader"/>
    /// with a hard <see cref="MaxLineChars"/> cap, so a hostile/wedged sidecar
    /// emitting one unbounded line can't OOM the host.
    ///
    /// <para>Reads a char at a time into <paramref name="buf"/> (cleared on
    /// entry). On '\n' the accumulated text (sans the trailing CR/LF) is
    /// returned via <paramref name="line"/> with outcome <see cref="ReadLineOk"/>.
    /// On EOF before any newline the partial is discarded and
    /// <see cref="ReadLineEof"/> is returned. If the buffer reaches
    /// <see cref="MaxLineChars"/> without a newline, the partial is dropped and
    /// the rest of the over-length line is consumed up to the next newline (or
    /// EOF) so the following line re-syncs; outcome is <see cref="ReadLineOverflow"/>.</para>
    ///
    /// <para>Char-at-a-time Read() is fine here: StreamReader buffers the
    /// underlying pipe internally, so each call is a cheap buffer index, not a
    /// syscall. The legit path is ~150 chars/line at 15 Hz.</para></summary>
    internal static int ReadBoundedLine(StreamReader reader, StringBuilder buf, out string line)
    {
        buf.Clear();
        while (true)
        {
            int c = reader.Read();
            if (c < 0)
            {
                // EOF. A trailing partial (no final newline) is dropped — the
                // production sidecar always terminates lines with '\n'.
                line = string.Empty;
                return ReadLineEof;
            }
            if (c == '\n')
            {
                // Strip a trailing CR so CRLF streams parse identically.
                if (buf.Length > 0 && buf[buf.Length - 1] == '\r')
                    buf.Length--;
                line = buf.ToString();
                return ReadLineOk;
            }
            if (buf.Length >= MaxLineChars)
            {
                // Over-length line with no newline yet: drop the partial and
                // swallow the remainder up to the next newline/EOF so the
                // stream re-syncs to a line boundary instead of mis-framing
                // the next legit line onto this giant one's tail.
                buf.Clear();
                while (true)
                {
                    int d = reader.Read();
                    if (d < 0 || d == '\n') break;
                }
                line = string.Empty;
                return ReadLineOverflow;
            }
            buf.Append((char)c);
        }
    }

    /// <summary>H36: advance the exponential-backoff state for a malformed
    /// (or over-length) line and report whether THIS occurrence should be
    /// logged. Increments the diagnostics total counter unconditionally.
    /// Logs the 1st bad line in a run, then the 2nd, 4th, 8th ... doubling the
    /// gap each time it emits, capped at one per <see cref="MalformedLogMaxInterval"/>.
    /// All backoff state is caller-owned (reader-Task-local) so no locking is
    /// needed; only the cross-thread total uses Interlocked.</summary>
    private bool NoteMalformedLine(ref long run, ref long nextLog, ref long interval)
    {
        Interlocked.Increment(ref _malformedTotal);
        run++;
        if (run < nextLog) return false;

        // Time to log. Schedule the next threshold by doubling the gap up to
        // the ceiling, so a sustained flood settles at 1 log / MalformedLog
        // MaxInterval lines.
        interval = Math.Min(interval * 2, MalformedLogMaxInterval);
        nextLog  = run + interval;
        return true;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Phase 3 fix (2026-05-30, adversarial review blocker #1): identity
        // check. .NET dispatches Process.Exited on a process-monitor thread
        // that is NOT serialized with our lifecycle gate. After Restart
        // resets _sidecarDead=0 and DoStart spawns a new Process, a
        // late-firing Exited callback for the PREVIOUS generation must NOT
        // write _sidecarDead=1 onto the new generation's shared state —
        // that would silently brick head-tracking until the user toggled
        // HeadTrackingEnabled OFF→ON in Settings.
        //
        // DoStop unsubscribes Exited before Kill (belt), but .NET may have
        // already queued this callback before the -= ran. The identity
        // check via ReferenceEquals against the volatile _process field
        // is the suspenders: a stale sender silently no-ops.
        if (sender is not Process p) return;
        if (!ReferenceEquals(p, _process))
        {
            // Stale callback from a previous-generation Process. Do nothing.
            return;
        }

        // Phase 3 fix-round-2 (2026-05-30, verification HIGH #B): collect
        // exit info using the LOCAL p reference (already identity-confirmed
        // for this generation), then re-check identity immediately before
        // the dead-flag write. This narrows the TOCTOU window between the
        // first identity check above and the Interlocked.Exchange to a
        // handful of instructions. Even if Restart races and swaps _process
        // between line A and line B below, the second check catches it and
        // we silently no-op rather than poisoning the new generation's
        // _sidecarDead flag.
        string tail;
        lock (_stderrLock) { tail = _stderrTail.ToString(); }
        int exitCode = -1;
        try { exitCode = p.ExitCode; } catch { }

        // Line A: gather state above. Line B: final identity guard immediately
        // before the publication side-effect.
        if (!ReferenceEquals(p, _process)) return;
        Interlocked.Exchange(ref _sidecarDead, 1);
        RefreshHealth(); // FEAT-08: unexpected exit → Dead transition
        // SEC-01: the stderr tail can carry full model/file paths from sidecar
        // FileNotFoundError messages — redact before it reaches the diag log.
        _log?.Invoke(PathRedactor.Redact($"[SidecarHeadPoseReader] Sidecar exited (code={exitCode}). stderr tail:\n{tail}"));
    }

    private void OnStderrLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (_stderrLock)
        {
            _stderrTail.AppendLine(e.Data);
            if (_stderrTail.Length > StderrTailMax)
            {
                int trim = _stderrTail.Length - StderrTailMax;
                _stderrTail.Remove(0, trim);
            }
        }
    }

    // ── JSON parsing ───────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        // H35: the sidecar schema is flat (depth 1 is the legit max). Cap depth
        // so a hostile/wedged sidecar can't drive the parser into deep nesting.
        MaxDepth                    = 8,
    };

    /// <summary>Internal DTO mirroring the sidecar's JSON schema with explicit
    /// snake_case attribute mapping. Kept private — consumers use the
    /// derived <see cref="HeadPoseSample"/> record from
    /// <see cref="SenseEase.Core.Abstractions"/>.
    ///
    /// <para>SIDE-06: <c>t_ms</c>, <c>cx</c>, and <c>cy</c> are nullable so
    /// an empty JSON object (<c>{}</c> or any document missing these required
    /// fields) is detected at parse time rather than silently corner-pinning to
    /// the (0, 0) default. <see cref="TryParseSampleLine"/> returns false when
    /// any of these three nulls is observed.</para></summary>
    internal sealed class SidecarFrameDto
    {
        // SIDE-06: required fields are nullable so missing-field detection
        // happens in TryParseSampleLine rather than defaulting to 0.
        [JsonPropertyName("t_ms")]
        public long? TMs { get; set; }

        [JsonPropertyName("cx")]
        public double? Cx { get; set; }

        [JsonPropertyName("cy")]
        public double? Cy { get; set; }

        [JsonPropertyName("luma")]
        public double Luma { get; set; }

        [JsonPropertyName("face_detected")]
        public bool FaceDetected { get; set; }

        [JsonPropertyName("face_present")]
        public double FacePresent { get; set; }

        // yaw_deg / pitch_deg are debug-only (G1 Q2). Present only when the
        // sidecar is launched with --debug. We do NOT carry them on the
        // public HeadPoseSample record (per the v1.0 schema lock), so they
        // are accepted-and-ignored here. Listed explicitly so a future test
        // could assert they round-trip cleanly without touching the host.
        [JsonPropertyName("yaw_deg")]
        public double? YawDeg { get; set; }

        [JsonPropertyName("pitch_deg")]
        public double? PitchDeg { get; set; }
    }

    /// <summary>Parse one sidecar stdout line into a <see cref="HeadPoseSample"/>.
    /// Returns true on success. Returns false (and sets sample to null) for:
    /// any JSON exception, an empty line, or a line that successfully
    /// deserializes but is missing required fields. This is the unit-testable
    /// seam — tests feed synthetic JSON lines through here without spawning
    /// a real process.
    ///
    /// <para>SIDE-06: <c>t_ms</c>, <c>cx</c>, and <c>cy</c> are required. An
    /// empty JSON object (<c>{}</c>) or any document missing any of these three
    /// fields returns false rather than silently corner-pinning to (0, 0).
    /// <c>cx</c>, <c>cy</c>, and <c>luma</c> are clamped to [0, 1] so an
    /// out-of-range sidecar cannot drive the mask outside the screen.</para></summary>
    internal static bool TryParseSampleLine(string line, out HeadPoseSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<SidecarFrameDto>(line, s_jsonOptions);
            if (dto is null) return false;

            // SIDE-06: reject when any required field is absent.
            if (dto.TMs is not { } tMs) return false;
            if (dto.Cx  is not { } cx)  return false;
            if (dto.Cy  is not { } cy)  return false;

            // SIDE-06: clamp cx/cy/luma to [0, 1] so an out-of-range sidecar
            // cannot drive the FOV mask outside the screen.
            static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;

            sample = new HeadPoseSample(
                TimestampMs:  tMs,
                Cx:           Clamp01(cx),
                Cy:           Clamp01(cy),
                Luma:         Clamp01(dto.Luma),
                FaceDetected: dto.FaceDetected,
                FacePresent:  dto.FacePresent);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // System.Text.Json wraps some converter errors here.
            return false;
        }
    }

    // ── Test hooks (internal — InternalsVisibleTo SenseEase.Tests) ─────────

    /// <summary>Test seam: directly inject a parsed sample as if the reader
    /// task had produced it. Used by tests to verify the latest-sample
    /// storage + staleness paths without spinning up a real process.</summary>
    internal void TestHook_StoreSample(HeadPoseSample sample, DateTime? storedAtUtc = null)
    {
        Volatile.Write(ref _latest, sample);
        // NE10: staleness now uses Environment.TickCount64 (monotonic). Preserve the
        // test contract (inject a sample aged by the storedAtUtc delta) by converting
        // that wall-clock age into a TickCount64 offset.
        long storedMs = storedAtUtc is { } t
            ? Environment.TickCount64 - (long)(DateTime.UtcNow - t).TotalMilliseconds
            : Environment.TickCount64;
        Interlocked.Exchange(ref _latestStoredAtMs, storedMs);
        // Beta-1 #10: mirror the FaceDetected freshness clock so health tests see the same
        // face-loss semantics as the live path (a FaceDetected sample marks the face fresh).
        if (sample.FaceDetected)
            Interlocked.Exchange(ref _latestFaceDetectedAtMs, storedMs);
        // Sample stored implies the reader is logically alive for the test.
        Interlocked.Exchange(ref _started, 1);
        Interlocked.Exchange(ref _sidecarDead, 0);
    }

    /// <summary>Test seam: simulate the sidecar process exiting. Mirrors the
    /// real <see cref="OnProcessExited"/> path so the
    /// SimulatedProcessExit_FallsBackToNullWithin2Seconds test can assert
    /// the fallback contract without taskkilling a real process.</summary>
    internal void TestHook_SimulateSidecarExit()
    {
        Interlocked.Exchange(ref _sidecarDead, 1);
    }

    /// <summary>Test seam: assert the reader hasn't been started yet. Used
    /// by the AppConfig_HeadTrackingEnabled_False invariant test.</summary>
    internal bool TestHook_IsStarted() => Volatile.Read(ref _started) == 1;
}
