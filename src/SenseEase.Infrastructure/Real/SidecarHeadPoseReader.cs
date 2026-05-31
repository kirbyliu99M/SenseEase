using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;

namespace SenseEase.Infrastructure.Real;

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
    private long _latestStoredAtUtcTicks;

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

    // UI Consolidation Sprint Phase 3 fix (2026-05-30, adversarial review):
    // serialization gate across Stop / Start / Restart / Dispose. Without
    // this, rapid Power Save toggling via MainWindow.ApplyPowerSave's
    // fire-and-forget Task.Run calls could (a) spawn an orphan sidecar
    // while UI shows Power Save ON (the D-B webcam-release privacy proof
    // breaks silently) or (b) leave the freshly-restarted sidecar dead
    // because a stale Stop wins the race against the new Start.
    // SemaphoreSlim(1,1) gives a simple non-reentrant mutex.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

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

    /// <summary>M5.1-C PRECONDITION: atomically read + reset the
    /// received-sample counter. Returns the count of successful parses since
    /// the last call (or since reader start). Safe to call from any thread.</summary>
    public int ConsumeReceivedSampleCount()
    {
        return Interlocked.Exchange(ref _receivedSampleCount, 0);
    }

    /// <summary>Construct the reader. Does NOT spawn the sidecar — call
    /// <see cref="Start"/> for that. Separating construction from startup
    /// keeps the flag-off path zero-cost: if HeadTrackingEnabled is false,
    /// nothing instantiates this class at all.</summary>
    /// <param name="pythonExePath">Absolute path to the Python interpreter
    /// (typically the sidecar's venv python.exe).</param>
    /// <param name="sidecarScriptPath">Absolute path to head_tracker.py.</param>
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
        _lifecycleGate.Wait();
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
                _log?.Invoke($"[SidecarHeadPoseReader] Frozen sidecar exe not found at '{_sidecarExePath}'. Sidecar disabled; ReadLatest will return null.");
                Interlocked.Exchange(ref _sidecarDead, 1);
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

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += OnProcessExited;
            proc.ErrorDataReceived += OnStderrLine;

            if (!proc.Start())
            {
                _log?.Invoke($"[SidecarHeadPoseReader] Process.Start returned false for '{_sidecarExePath} {args}'. Sidecar disabled.");
                Interlocked.Exchange(ref _sidecarDead, 1);
                return;
            }
            proc.BeginErrorReadLine();

            // Reap the sidecar via the existing Windows JobObject infra so it
            // dies with us even on a hard host crash. Same pattern used for
            // llama-server. Best-effort — failure here is non-fatal.
            try { ChildProcessReaper.Adopt(proc); } catch { }

            _process = proc;
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _readerTask = Task.Factory.StartNew(
                () => ReaderLoop(proc, ct),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            _log?.Invoke($"[SidecarHeadPoseReader] Started pid={proc.Id} '{_sidecarExePath} {args}'");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[SidecarHeadPoseReader] Start failed: {ex.GetType().Name}: {ex.Message}. Sidecar disabled.");
            Interlocked.Exchange(ref _sidecarDead, 1);
        }
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

        long storedAt = Interlocked.Read(ref _latestStoredAtUtcTicks);
        long nowTicks = DateTime.UtcNow.Ticks;
        long ageMs    = (nowTicks - storedAt) / TimeSpan.TicksPerMillisecond;
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
        _lifecycleGate.Wait();
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

        // Phase 3 fix (2026-05-30): acquire the lifecycle gate so any
        // in-flight Power-Save-triggered Stop/Restart finishes before we
        // tear down. Then call DoStop (not public Stop — we're already
        // inside the gate). Finally dispose the gate AFTER releasing it.
        try
        {
            _lifecycleGate.Wait();
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
        _lifecycleGate.Wait();
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
            Interlocked.Exchange(ref _latestStoredAtUtcTicks, 0);
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

                string? line;
                try
                {
                    line = stdout.ReadLine();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[SidecarHeadPoseReader] stdout ReadLine threw {ex.GetType().Name}: {ex.Message}. Reader exiting.");
                    break;
                }
                if (line is null) break;          // EOF — sidecar closed stdout.
                if (line.Length == 0) continue;   // skip empty lines defensively.

                if (TryParseSampleLine(line, out HeadPoseSample? sample) && sample is not null)
                {
                    Volatile.Write(ref _latest, sample);
                    Interlocked.Exchange(ref _latestStoredAtUtcTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref _receivedSampleCount); // M5.1-C
                }
                else
                {
                    // Malformed line: log at debug level and continue. Keep
                    // the previous _latest so a single bad line doesn't trip
                    // the staleness fallback. This is the "fuzz-resilient"
                    // behavior asserted by SidecarHeadPoseReader_Parses
                    // MalformedLine_DoesNotThrow.
                    _log?.Invoke($"[SidecarHeadPoseReader] Skipped malformed line (len={line.Length}).");
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
            Interlocked.Exchange(ref _sidecarDead, 1);
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
        _log?.Invoke($"[SidecarHeadPoseReader] Sidecar exited (code={exitCode}). stderr tail:\n{tail}");
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
    };

    /// <summary>Internal DTO mirroring the sidecar's JSON schema with explicit
    /// snake_case attribute mapping. Kept private — consumers use the
    /// derived <see cref="HeadPoseSample"/> record from
    /// <see cref="SenseEase.Core.Abstractions"/>.</summary>
    internal sealed class SidecarFrameDto
    {
        [JsonPropertyName("t_ms")]
        public long TMs { get; set; }

        [JsonPropertyName("cx")]
        public double Cx { get; set; }

        [JsonPropertyName("cy")]
        public double Cy { get; set; }

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
    /// a real process.</summary>
    internal static bool TryParseSampleLine(string line, out HeadPoseSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<SidecarFrameDto>(line, s_jsonOptions);
            if (dto is null) return false;
            sample = new HeadPoseSample(
                TimestampMs:  dto.TMs,
                Cx:           dto.Cx,
                Cy:           dto.Cy,
                Luma:         dto.Luma,
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
        Interlocked.Exchange(ref _latestStoredAtUtcTicks,
            (storedAtUtc ?? DateTime.UtcNow).Ticks);
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
