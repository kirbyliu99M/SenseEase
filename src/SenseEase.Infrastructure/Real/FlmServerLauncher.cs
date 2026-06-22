using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Ultrabook plan #5 — brings up (or reuses) a FastFlowLM OpenAI-compatible server for a model tag on the
/// AMD Ryzen-AI NPU, so SenseEase can drive it over the external-backend seam (see
/// <see cref="LocalLlmChatbotService"/>). Everything here is best-effort and NON-THROWING: if flm.exe is
/// absent, the model isn't installed, or the server never answers, <see cref="TryAcquireAsync"/> returns
/// null and the caller falls back to the bundled Vulkan server.
///
/// Ownership: we REUSE an already-listening server (OwnedProcess = null → never killed — it isn't ours), and
/// only OWN a server we launch ourselves (OwnedProcess set → the chatbot kills it on Dispose).
/// </summary>
public static class FlmServerLauncher
{
    /// <summary>FastFlowLM's fixed default server port (<c>flm port</c> → 52625, stable across releases). A
    /// constant avoids spawning a probe process on the hot path.</summary>
    public const int DefaultPort = 52625;

    // Where the FastFlowLM installer drops flm.exe (it also adds this to the User PATH).
    private static readonly string[] ExeCandidates =
    {
        @"C:\Program Files\flm\flm.exe",
        @"C:\Program Files (x86)\flm\flm.exe",
    };

    // One short-lived probe client for the readiness GETs; the per-call timeout is enforced via a linked CTS.
    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Relative path, under the app's own install dir, where the Phase-3 AMD pack drops the bundled
    /// FastFlowLM runtime. A per-user install (<c>PrivilegesRequired=lowest</c>) can write neither
    /// <c>C:\Program Files\flm</c> nor the machine PATH, so the installer lays flm into the app tree and we
    /// probe HERE FIRST — this is the only place a bundled-FLM Beta install can be found.</summary>
    private const string BundledFlmRelative = @"packs\amd-flm\flm\flm.exe";

    /// <summary>Locate flm.exe: the app's own bundled AMD pack first (Phase-3 per-user install), then PATH
    /// (where the standalone FastFlowLM installer adds it), then the standard install dirs. Null when
    /// FastFlowLM isn't available anywhere → no NPU backend, the caller stays on Vulkan.</summary>
    public static string? FindExe()
    {
        // 1) The flm runtime SenseEase's own installer bundled into the app dir (Phase-3 AMD pack).
        try
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, BundledFlmRelative);
            if (File.Exists(bundled)) return bundled;
        }
        catch { /* BaseDirectory unavailable in some hosts — fall through to PATH / install dirs */ }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var raw in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string candidate;
                try { candidate = Path.Combine(raw.Trim(), "flm.exe"); } catch { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        foreach (var c in ExeCandidates) if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Acquire a FastFlowLM backend for <paramref name="modelTag"/>: reuse a server already
    /// listening on <paramref name="port"/>, else launch <c>flm serve &lt;tag&gt; --port &lt;port&gt;</c> and
    /// wait for it to answer. Returns null (→ Vulkan fallback) when flm is missing, the model isn't installed,
    /// or the server never comes up. Never throws.</summary>
    public static async Task<ManagedBackend?> TryAcquireAsync(
        string modelTag, int port = DefaultPort, Action<string>? trace = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelTag)) return null;
        string baseUrl = $"http://127.0.0.1:{port}";
        try
        {
            // 1) Reuse an already-running server (a prior app run, or the user ran `flm serve`). We can't read
            //    WHICH model is loaded from /v1/models (it lists the whole catalog), so we trust the port: in
            //    production the only thing on it is SenseEase's own previous launch of this same tag.
            if (await IsServerUpAsync(baseUrl, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            {
                trace?.Invoke($"FLM: reusing server already on {baseUrl}");
                return new ManagedBackend(baseUrl, modelTag, OwnedProcess: null);
            }

            // 2) We need flm.exe to launch our own.
            string? exe = FindExe();
            if (exe is null) { trace?.Invoke("FLM: flm.exe not found — using Vulkan"); return null; }

            // 3) Never trigger a multi-GB auto-pull at startup: only launch if the model is already installed.
            if (!IsModelInstalled(exe, modelTag))
            {
                trace?.Invoke($"FLM: model '{modelTag}' not installed — using Vulkan");
                return null;
            }

            // 4) Launch and wait for readiness.
            Process? proc = TryStartServe(exe, modelTag, port, trace);
            if (proc is null) return null;
            if (await IsServerUpAsync(baseUrl, TimeSpan.FromSeconds(45), ct, pollEvery: TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false))
            {
                trace?.Invoke($"FLM: launched + serving '{modelTag}' on {baseUrl} (NPU)");
                return new ManagedBackend(baseUrl, modelTag, OwnedProcess: proc);
            }

            // Never answered — don't leak the process.
            trace?.Invoke($"FLM: '{modelTag}' did not come up — killing + using Vulkan");
            KillQuietly(proc);
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { trace?.Invoke($"FLM: acquire failed ({ex.GetType().Name}) — using Vulkan"); return null; }
    }

    /// <summary>True if a server answers <c>GET /v1/models</c> within <paramref name="overall"/>, polling every
    /// <paramref name="pollEvery"/> (default: a single attempt). Never throws.</summary>
    private static async Task<bool> IsServerUpAsync(
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
                    using var resp = await _probe.GetAsync($"{baseUrl}/v1/models", cts.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { /* not up yet */ }
            }
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline) return false;
            try { await Task.Delay(step, ct).ConfigureAwait(false); } catch { return false; }
        }
    }

    /// <summary>Whether <c>flm list --filter installed</c> reports <paramref name="modelTag"/>. Bounded +
    /// non-throwing; a missing/slow flm resolves to false (→ no launch).</summary>
    private static bool IsModelInstalled(string exe, string modelTag)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, "list --filter installed")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
                },
            };
            if (!p.Start()) return false;
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { KillQuietly(p); return false; }
            return stdout.IndexOf(modelTag, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>Start <c>flm serve &lt;tag&gt; --port &lt;port&gt;</c> headless, draining its (verbose) stdio so
    /// the pipe buffer can't fill and stall the server. Null on any start failure.</summary>
    private static Process? TryStartServe(string exe, string modelTag, int port, Action<string>? trace)
    {
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, $"serve {modelTag} --port {port}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
                },
            };
            p.OutputDataReceived += (_, __) => { };   // drain — avoid a full-pipe deadlock on the chatty log
            p.ErrorDataReceived += (_, __) => { };
            if (!p.Start()) return null;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // Reap the flm child if SenseEase exits OR crashes (Job Object KILL_ON_JOB_CLOSE), matching the
            // Vulkan server — so a crash never leaves an orphan flm holding the NPU. Best-effort.
            try { ChildProcessReaper.Adopt(p); } catch { }
            return p;
        }
        catch (Exception ex) { trace?.Invoke($"FLM: serve start failed ({ex.GetType().Name})"); return null; }
    }

    private static void KillQuietly(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }
}
