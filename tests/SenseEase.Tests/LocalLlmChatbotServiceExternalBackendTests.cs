using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Ultrabook plan #5 + #38a — the external-backend seam: SenseEase connects to an already-running OpenAI
/// server (e.g. FastFlowLM on the AMD NPU, or the Intel OpenVINO GenAI host) instead of launching the bundled
/// Vulkan server. These pin the GRACEFUL-FAILURE + READINESS contract deterministically:
///   • an unreachable server leaves the chatbot unloaded (→ deterministic lexicon) and never throws;
///   • readiness is gated on <c>/health</c> (status=ready), NOT <c>/v1/models</c> — the OV host answers
///     <c>/v1/models</c> 200 the instant its socket binds, long before the NPU blob is compiled, so gating on
///     it would mark the model ready mid-compile and silently route the first turns to the lexicon;
///   • a host WITHOUT <c>/health</c> (404) falls back to the legacy <c>/v1/models</c> probe (FLM-safe).
/// </summary>
public class LocalLlmChatbotServiceExternalBackendTests
{
    [Fact]
    public async Task ExternalMode_UnreachableServer_StaysUnloaded_NoThrow()
    {
        // External mode is selected purely by the base URL — the local GGUF need not exist.
        using var svc = new LocalLlmChatbotService(
            modelPath: "C:/nonexistent/senseease-test-model.gguf",
            externalBaseUrl: "http://127.0.0.1:1",     // nothing listens on port 1
            externalModelName: "qwen3:4b",
            externalReadyBudget: TimeSpan.Zero);        // single probe — keep the failure path fast

        await svc.LoadAsync();                          // connect attempt — must not throw

        Assert.False(svc.IsModelLoaded);                // unreachable → not loaded
        Assert.NotNull(svc.LoadError);                  // and a diagnostic is recorded
    }

    [Fact]
    public async Task HealthReady_LoadsAndMarksBackend()
    {
        using var host = new FakeOpenAiHost(18241, (path, res) =>
        {
            if (path == "/health") return (200, "{\"status\":\"ready\",\"device\":\"NPU\"}");
            if (path == "/v1/models") return (200, "{\"object\":\"list\",\"data\":[]}");
            return (404, "{}");
        });

        using var svc = new LocalLlmChatbotService(
            modelPath: "C:/nonexistent/senseease-test-model.gguf",
            externalBaseUrl: host.BaseUrl, externalModelName: "qwen3-4b",
            externalReadyBudget: TimeSpan.FromSeconds(2));

        await svc.LoadAsync();

        Assert.True(svc.IsModelLoaded);
        Assert.Null(svc.LoadError);
        Assert.Equal("external server (env override)", svc.ActiveBackend);
    }

    [Fact]
    public async Task HealthCompiling_DoesNotFalseStartOnV1Models()
    {
        // The crux of the #38a bug: /v1/models answers 200 while the host is still cold-compiling. The service
        // must NOT mark loaded off that — it must wait on /health, and stay unloaded if it never goes ready.
        using var host = new FakeOpenAiHost(18242, (path, res) =>
        {
            if (path == "/health") return (503, "{\"status\":\"compiling\",\"device\":\"NPU\"}");
            if (path == "/v1/models") return (200, "{\"object\":\"list\",\"data\":[{\"id\":\"qwen3-4b\"}]}");
            return (404, "{}");
        });

        using var svc = new LocalLlmChatbotService(
            modelPath: "C:/nonexistent/senseease-test-model.gguf",
            externalBaseUrl: host.BaseUrl, externalModelName: "qwen3-4b",
            externalReadyBudget: TimeSpan.Zero);        // single probe — sees compiling, must stay unloaded

        await svc.LoadAsync();

        Assert.False(svc.IsModelLoaded);                // did NOT false-start on the 200 /v1/models
        Assert.NotNull(svc.LoadError);
    }

    [Fact]
    public async Task HealthCompiling_ThenReady_WaitsAndLoads()
    {
        int probes = 0;
        using var host = new FakeOpenAiHost(18243, (path, res) =>
        {
            if (path == "/health")
            {
                int n = Interlocked.Increment(ref probes);
                return n >= 2 ? (200, "{\"status\":\"ready\"}") : (503, "{\"status\":\"compiling\"}");
            }
            return (404, "{}");
        });

        using var svc = new LocalLlmChatbotService(
            modelPath: "C:/nonexistent/senseease-test-model.gguf",
            externalBaseUrl: host.BaseUrl, externalModelName: "qwen3-4b",
            externalReadyBudget: TimeSpan.FromSeconds(5));   // budget covers the ~1s poll to the 2nd probe

        await svc.LoadAsync();

        Assert.True(svc.IsModelLoaded);                 // polled through 'compiling' to 'ready'
        Assert.True(probes >= 2);
    }

    [Fact]
    public async Task NoHealthEndpoint_FallsBackToV1Models()
    {
        // A host without /health (404) — a plain llama.cpp / FastFlowLM server. Must use the legacy
        // /v1/models readiness probe so existing backends keep loading exactly as before.
        using var host = new FakeOpenAiHost(18244, (path, res) =>
        {
            if (path == "/health") return (404, "{\"error\":\"not found\"}");
            if (path == "/v1/models") return (200, "{\"object\":\"list\",\"data\":[{\"id\":\"qwen3-it:4b\"}]}");
            return (404, "{}");
        });

        using var svc = new LocalLlmChatbotService(
            modelPath: "C:/nonexistent/senseease-test-model.gguf",
            externalBaseUrl: host.BaseUrl, externalModelName: "qwen3-it:4b",
            externalReadyBudget: TimeSpan.FromSeconds(2));

        await svc.LoadAsync();

        Assert.True(svc.IsModelLoaded);                 // loaded via the /v1/models fallback
        Assert.Null(svc.LoadError);
    }

    [Theory]
    // status field wins
    [InlineData("{\"status\":\"ready\"}", true, LocalLlmChatbotService.ExternalHealth.Ready)]
    [InlineData("{\"status\":\"ok\"}", true, LocalLlmChatbotService.ExternalHealth.Ready)]
    [InlineData("{\"status\":\"compiling\"}", false, LocalLlmChatbotService.ExternalHealth.Compiling)]
    [InlineData("{\"status\":\"loading model\"}", false, LocalLlmChatbotService.ExternalHealth.Compiling)]
    [InlineData("{\"status\":\"error\",\"error\":\"x\"}", false, LocalLlmChatbotService.ExternalHealth.Error)]
    // no/garbage status → fall back to the HTTP code
    [InlineData("", true, LocalLlmChatbotService.ExternalHealth.Ready)]
    [InlineData("", false, LocalLlmChatbotService.ExternalHealth.Compiling)]
    [InlineData("not json", true, LocalLlmChatbotService.ExternalHealth.Ready)]
    [InlineData("{\"foo\":1}", false, LocalLlmChatbotService.ExternalHealth.Compiling)]
    public void ParseExternalHealth_Matrix(string body, bool httpOk, LocalLlmChatbotService.ExternalHealth expected)
        => Assert.Equal(expected, LocalLlmChatbotService.ParseExternalHealth(body, httpOk));

    [Theory]
    [InlineData(true, true, true, true)]     // loaded + we own the process + it exited (crash) → re-acquire
    [InlineData(true, true, false, false)]   // loaded + owned + healthy → leave it
    [InlineData(true, false, false, false)]  // loaded + reused/env-override (no owned process) → never re-acquire
    [InlineData(false, true, true, false)]   // not loaded → the normal reload-on-demand path handles it
    public void ShouldReacquireOwnedProcess_TruthTable(bool loaded, bool owned, bool exited, bool expected)
        => Assert.Equal(expected, LocalLlmChatbotService.ShouldReacquireOwnedProcess(loaded, owned, exited));

    /// <summary>A throwaway loopback OpenAI-ish host for readiness tests: dispatches by request path through a
    /// caller-supplied responder (returns (statusCode, jsonBody)). Loopback prefix needs no admin/URL ACL.</summary>
    private sealed class FakeOpenAiHost : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public string BaseUrl { get; }

        public FakeOpenAiHost(int port, Func<string, HttpListenerResponse, (int, string)> respond)
        {
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                    catch { break; }   // listener stopped
                    try
                    {
                        var (code, json) = respond(ctx.Request.Url!.AbsolutePath, ctx.Response);
                        byte[] buf = System.Text.Encoding.UTF8.GetBytes(json);
                        ctx.Response.StatusCode = code;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = buf.Length;
                        await ctx.Response.OutputStream.WriteAsync(buf).ConfigureAwait(false);
                        ctx.Response.Close();
                    }
                    catch { /* client gone — ignore */ }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }
    }
}
