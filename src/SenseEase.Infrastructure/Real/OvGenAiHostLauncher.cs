using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// #38c — brings up (or reuses) the Intel OpenVINO GenAI host: an OpenAI-compatible local server backed by
/// <c>ov::genai::LLMPipeline</c> serving the INT4 IR of Qwen3-4B on the Intel NPU/iGPU (the 2026-06-24 pivot:
/// the retired ggml-openvino+GGUF path is replaced by this host + IR). SenseEase drives it over the SAME
/// external-backend seam as FastFlowLM (see <see cref="LocalLlmChatbotService"/> + <see cref="FlmServerLauncher"/>).
///
/// Everything here is best-effort and NON-THROWING: if the host exe or the IR model is absent, or the server
/// never binds its socket, <see cref="TryAcquireAsync"/> returns null and the caller falls back to the bundled
/// Vulkan server (kept as the universal fallback). Ownership mirrors the FLM launcher: we REUSE an
/// already-listening host (OwnedProcess = null → never killed), and only OWN a host we launch ourselves.
///
/// <para><b>Device auto-probe (decision #1)</b> lives in the host, not here: the host requests
/// <c>OVHOST_DEVICE=NPU</c> and self-falls-back to the iGPU on a load-time OR generate-time NPU failure (same
/// IR, equivalent quality). So this launcher just requests NPU and confirms the socket bound; the cold-compile
/// READY wait (≈237 s on a cache miss) happens in <see cref="LocalLlmChatbotService"/> via the long
/// <c>externalReadyBudget</c> the Intel path passes — gated on <c>/health=ready</c>, never <c>/v1/models</c>.</para>
/// </summary>
public static class OvGenAiHostLauncher
{
    /// <summary>The host's fixed default port (OVHOST_PORT → 8181), matching the validated reference host.</summary>
    public const int DefaultPort = 8181;

    /// <summary>The model id the host reports + the chatbot sends as the request "model" (OVHOST_MODELID).</summary>
    public const string DefaultModelId = "qwen3-4b";

    // One short-lived probe client for the readiness GETs; the per-call timeout is enforced via a linked CTS.
    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Relative path, under the app's own install dir, where the Phase-3 Intel pack drops the GenAI
    /// host exe (the Python interim host, or the native C++ sidecar) — mirrors the FLM pack convention
    /// (<c>packs\amd-flm\flm\flm.exe</c>). A per-user install can't write Program Files, so the installer lays
    /// the host into the app tree and we probe HERE.</summary>
    private const string BundledHostRelative = @"packs\intel-ov\host\genai_host.exe";

    /// <summary>Relative path, under the app's own install dir, of the bundled OpenVINO IR model directory.</summary>
    private const string BundledModelRelative = @"packs\intel-ov\model";

    /// <summary>Locate the GenAI host exe: an explicit <c>SENSEEASE_OVHOST_EXE</c> override (dev/field) first,
    /// then the app's own bundled Intel pack. Null when neither is present → no OV host, the caller stays on
    /// Vulkan.</summary>
    public static string? FindExe()
    {
        string? overrideExe = Environment.GetEnvironmentVariable("SENSEEASE_OVHOST_EXE");
        if (!string.IsNullOrWhiteSpace(overrideExe))
        {
            try { if (File.Exists(overrideExe)) return overrideExe; } catch { /* fall through */ }
        }
        try
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, BundledHostRelative);
            if (File.Exists(bundled)) return bundled;
        }
        catch { /* BaseDirectory unavailable in some hosts */ }
        return null;
    }

    /// <summary>Locate the OpenVINO IR model directory: an explicit <c>SENSEEASE_OVHOST_MODEL</c> /
    /// <c>OVHOST_MODEL</c> override first, then the bundled Intel pack. Null when neither exists.</summary>
    public static string? FindModelDir()
    {
        foreach (var key in new[] { "SENSEEASE_OVHOST_MODEL", "OVHOST_MODEL" })
        {
            string? m = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(m))
            {
                try { if (Directory.Exists(m)) return m; } catch { /* fall through */ }
            }
        }
        try
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, BundledModelRelative);
            if (Directory.Exists(bundled)) return bundled;
        }
        catch { /* BaseDirectory unavailable */ }
        return null;
    }

    /// <summary>Acquire the OV GenAI host for <paramref name="modelId"/>: reuse a host already listening on
    /// <paramref name="port"/>, else launch the bundled host (with the IR model + device env) and wait for its
    /// socket to bind. Returns null (→ Vulkan fallback) when the host exe / IR model is missing or the server
    /// never comes up. Never throws.</summary>
    public static async Task<ManagedBackend?> TryAcquireAsync(
        string modelId, string device = "NPU", int port = DefaultPort,
        Action<string>? trace = null, CancellationToken ct = default)
    {
        string baseUrl = $"http://127.0.0.1:{port}";
        try
        {
            // 1) Reuse a host already listening (a prior app run, or a dev/field-run host on the same port).
            if (await IsHostListeningAsync(baseUrl, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            {
                trace?.Invoke($"OV host: reusing server already on {baseUrl}");
                return new ManagedBackend(baseUrl, modelId, OwnedProcess: null);
            }

            // 2) We need the host exe AND the IR model to launch our own.
            string? exe = FindExe();
            if (exe is null) { trace?.Invoke("OV host: genai host exe not found — using Vulkan"); return null; }
            string? model = FindModelDir();
            if (model is null) { trace?.Invoke("OV host: IR model dir not found — using Vulkan"); return null; }

            // 3) Launch + wait only for the socket to BIND (/health reachable, any status). The cold-compile
            //    READY wait is owned by ConnectExternalAsync (the Intel path passes a long readiness budget);
            //    the host self-falls-back NPU→iGPU at load, so we just request the requested device here.
            Process? proc = TryStartHost(exe, model, device, port, trace);
            if (proc is null) return null;
            if (await IsHostListeningAsync(baseUrl, TimeSpan.FromSeconds(20), ct, pollEvery: TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false))
            {
                trace?.Invoke($"OV host: launched on {baseUrl} (device={device}; compiling the IR in the background)");
                return new ManagedBackend(baseUrl, modelId, OwnedProcess: proc);
            }

            trace?.Invoke("OV host: socket never bound — killing + using Vulkan");
            KillQuietly(proc);
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { trace?.Invoke($"OV host: acquire failed ({ex.GetType().Name}) — using Vulkan"); return null; }
    }

    /// <summary>True if the host answers <c>GET /health</c> with ANY status (200 ready / 503 compiling) within
    /// <paramref name="overall"/> — i.e. the socket is bound. A connection refusal means it isn't up yet.
    /// Never throws.</summary>
    private static async Task<bool> IsHostListeningAsync(
        string baseUrl, TimeSpan overall, CancellationToken ct, TimeSpan? pollEvery = null)
    {
        var deadline = DateTime.UtcNow + overall;
        var step = pollEvery ?? overall;
        while (true)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(step < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : step);
                try
                {
                    using var resp = await _probe.GetAsync($"{baseUrl}/health", cts.Token).ConfigureAwait(false);
                    return true;   // any HTTP response means the socket is bound (compiling counts as listening)
                }
                catch { /* not up yet */ }
            }
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) return false;
            try { await Task.Delay(step, ct).ConfigureAwait(false); } catch { return false; }
        }
    }

    /// <summary>Start the GenAI host headless with the model + device + cache env, draining its stdio so the
    /// pipe buffer can't fill and stall it. Null on any start failure.</summary>
    private static Process? TryStartHost(string exe, string modelDir, string device, int port, Action<string>? trace)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            psi.EnvironmentVariables["OVHOST_MODEL"]   = modelDir;
            psi.EnvironmentVariables["OVHOST_DEVICE"]  = device;
            psi.EnvironmentVariables["OVHOST_PORT"]    = port.ToString();
            psi.EnvironmentVariables["OVHOST_MODELID"] = DefaultModelId;
            // Per-user-writable CACHE_DIR (the host's C:\ovgenai default isn't writable on a per-user install).
            // The compiled NPU blob lands here; a shipped pre-built blob can be seeded into this dir (decision #3).
            try
            {
                string cache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SenseEase", "ov_cache");
                psi.EnvironmentVariables["OVHOST_CACHE"] = cache;
            }
            catch { /* fall back to the host's own default */ }

            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, __) => { };   // drain — avoid a full-pipe deadlock on the chatty log
            p.ErrorDataReceived += (_, __) => { };
            if (!p.Start()) return null;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // Reap the host child if SenseEase exits OR crashes (Job Object KILL_ON_JOB_CLOSE), matching the
            // Vulkan/FLM servers — so a crash never leaves an orphan host holding the NPU. Best-effort.
            try { ChildProcessReaper.Adopt(p); } catch { }
            return p;
        }
        catch (Exception ex) { trace?.Invoke($"OV host: start failed ({ex.GetType().Name})"); return null; }
    }

    private static void KillQuietly(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }
}
