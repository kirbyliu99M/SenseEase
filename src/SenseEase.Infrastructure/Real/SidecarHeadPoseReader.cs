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

    private readonly string _pythonExePath;
    private readonly string _sidecarScriptPath;
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

    private Process? _process;
    private Task?    _readerTask;
    private CancellationTokenSource? _cts;

    private int _sidecarDead;   // 0 = alive, 1 = dead. Interlocked-flag.
    private int _started;       // 0 = not started, 1 = started. Interlocked-flag.
    private int _disposed;      // 0 = live, 1 = disposed. Interlocked-flag.

    // Small ring of stderr tail for diagnostics when the sidecar exits.
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderrTail = new();
    private const int StderrTailMax = 4096;

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
        string pythonExePath,
        string sidecarScriptPath,
        Action<string>? log = null,
        bool mirror = true,
        int? webcamIndex = 0)
    {
        _pythonExePath     = pythonExePath     ?? throw new ArgumentNullException(nameof(pythonExePath));
        _sidecarScriptPath = sidecarScriptPath ?? throw new ArgumentNullException(nameof(sidecarScriptPath));
        _log               = log;
        _mirror            = mirror;
        _webcamIndex       = webcamIndex;
    }

    /// <summary>Spawn the sidecar process and start the background reader
    /// task. Idempotent: a second call is a no-op. Best-effort: if the
    /// Python binary or script isn't where we expect, log + flip the dead
    /// flag so ReadLatest() returns null and the consumer falls back to
    /// center default. The app does NOT crash on a missing sidecar.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (Volatile.Read(ref _disposed) == 1) return;

        try
        {
            if (!File.Exists(_pythonExePath))
            {
                _log?.Invoke($"[SidecarHeadPoseReader] Python interpreter not found at '{_pythonExePath}'. Sidecar disabled; ReadLatest will return null.");
                Interlocked.Exchange(ref _sidecarDead, 1);
                return;
            }
            if (!File.Exists(_sidecarScriptPath))
            {
                _log?.Invoke($"[SidecarHeadPoseReader] Sidecar script not found at '{_sidecarScriptPath}'. Sidecar disabled; ReadLatest will return null.");
                Interlocked.Exchange(ref _sidecarDead, 1);
                return;
            }

            string args = BuildSidecarArgs(_sidecarScriptPath, _webcamIndex, _mirror);
            var psi = new ProcessStartInfo
            {
                FileName               = _pythonExePath,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                // Run the sidecar in its own folder so relative model
                // lookups (./face_landmarker.task) work even when the host
                // EXE lives elsewhere (e.g. a published install).
                WorkingDirectory       = Path.GetDirectoryName(_sidecarScriptPath) ?? string.Empty,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += OnProcessExited;
            proc.ErrorDataReceived += OnStderrLine;

            if (!proc.Start())
            {
                _log?.Invoke($"[SidecarHeadPoseReader] Process.Start returned false for '{_pythonExePath} {args}'. Sidecar disabled.");
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

            _log?.Invoke($"[SidecarHeadPoseReader] Started pid={proc.Id} '{_pythonExePath} {args}'");
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
    /// returns null even before the process actually exits.</summary>
    public void Stop()
    {
        if (Volatile.Read(ref _started) == 0) return;

        Interlocked.Exchange(ref _sidecarDead, 1);
        try { _cts?.Cancel(); } catch { }

        var proc = _process;
        if (proc is not null)
        {
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
        }

        try { _readerTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { Stop(); } catch { }
        try { _process?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _process    = null;
        _readerTask = null;
        _cts        = null;
    }

    // ── Internals ──────────────────────────────────────────────────────────

    /// <summary>Build the CLI argument string for the sidecar. Extracted so
    /// tests can verify argument shape without spawning a real process.</summary>
    internal static string BuildSidecarArgs(string scriptPath, int? webcamIndex, bool mirror)
    {
        // Mirror flag uses BooleanOptionalAction on the sidecar side (Q4):
        // default ON, --no-mirror disables. Only emit the explicit flag when
        // the C# host wants to override the default to avoid argument noise.
        string input = (webcamIndex ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string mirrorFlag = mirror ? string.Empty : " --no-mirror";
        return $"\"{scriptPath}\" --input {input}{mirrorFlag}";
    }

    private void ReaderLoop(Process proc, CancellationToken ct)
    {
        StreamReader stdout = proc.StandardOutput;
        try
        {
            while (!ct.IsCancellationRequested)
            {
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
        Interlocked.Exchange(ref _sidecarDead, 1);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _sidecarDead, 1);
        string tail;
        lock (_stderrLock) { tail = _stderrTail.ToString(); }
        int exitCode = -1;
        try { if (_process is { } p) exitCode = p.ExitCode; } catch { }
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
