using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// H9/H10: process-lifetime holder for the random Bearer API key that
/// <see cref="MistralChatbotService"/> generates when it spawns llama-server.
/// The server is launched with <c>--api-key &lt;key&gt;</c> so every HTTP caller
/// (the NLU service AND this calibration client) must present the SAME key. The
/// client is constructed via a parameterless <c>new LlamaServerCalibrationClient()</c>
/// at a call site we cannot modify, so it reads the key from here at request time
/// rather than receiving it through its constructor.
///
/// <para>Volatile single-writer (MistralChatbotService.LoadAsync) / many-reader
/// (each SendChatAsync) — no lock needed for a reference-typed string publish.</para>
/// </summary>
internal static class LlamaSessionKey
{
    private static volatile string? _current;

    /// <summary>The active server API key, or null if the server has not been
    /// brought up yet (in which case the client sends no Authorization header and
    /// the request simply fails closed against a key-protected server).</summary>
    public static string? Current => _current;

    /// <summary>Publish the key generated at server spawn. Idempotent per spawn.</summary>
    public static void Set(string? key) => _current = key;
}

// ─────────────────────────────────────────────────────────────────────────────
// Production ICalibrationLlmClient — posts to the running llama-server's
// OpenAI-compatible /v1/chat/completions on http://127.0.0.1:8181. Mirrors
// MistralChatbotService's HTTP pattern (private HttpClient with a long
// timeout, JSON deserialization, choices[0].message.content extraction).
//
// Does NOT spawn a server — only talks to one MistralChatbotService already
// brought up. When the server isn't running, calls return null and the
// caller (the User-Model retrospective analyzer) treats it as "LLM unavailable".
//
// (The calibration card that originally used this was removed 2026-06-08; the
// HTTP client is generic local-LLM plumbing, now consumed by
// UserModelRetrospectiveAnalyzer via ICalibrationLlmClient.)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LlamaServerCalibrationClient : ICalibrationLlmClient, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const int ServerPort = 8181;
    private string ServerUrl => $"http://127.0.0.1:{ServerPort}";

    // H9/H10: explicit key override (constructor-supplied). When null we fall back
    // to the process-lifetime LlamaSessionKey holder published by the NLU service
    // at server spawn. Resolved per request so a key that arrives AFTER this client
    // is constructed (the common case: client built before the server is ready) is
    // still picked up.
    private readonly string? _explicitApiKey;

    /// <summary>Parameterless ctor (existing call sites) — shares the server API
    /// key via the <see cref="LlamaSessionKey"/> holder.</summary>
    public LlamaServerCalibrationClient() : this(null) { }

    /// <summary>H9/H10: ctor that pins an explicit Bearer key (takes precedence
    /// over the shared holder). Lets tests/alternate hosts inject a known key.</summary>
    public LlamaServerCalibrationClient(string? apiKey)
    {
        _explicitApiKey = apiKey;
    }

    private string? ResolveApiKey() => _explicitApiKey ?? LlamaSessionKey.Current;

    public async Task<string?> SendChatAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        if (systemPrompt is null) throw new ArgumentNullException(nameof(systemPrompt));
        if (history is null) throw new ArgumentNullException(nameof(history));

        var messages = new List<object>(history.Count + 1)
        {
            new { role = "system", content = systemPrompt },
        };
        foreach (var m in history)
        {
            messages.Add(new
            {
                role = m.Role == ChatRole.User ? "user" : "assistant",
                content = m.Content,
            });
        }

        var requestBody = new
        {
            messages = messages.ToArray(),
            max_tokens = 768, // larger budget than NLU path — calibration replies include the JSON delta block
            temperature = 0.4,
            top_p = 0.9,
            top_k = 40,
            stop = new[] { "[INST]" },
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{ServerUrl}/v1/chat/completions")
            {
                Content = JsonContent.Create(requestBody),
            };
            // H9/H10: present the shared Bearer key so a key-protected llama-server
            // accepts this calibration call. Resolved per request (the key may be
            // published after this client was constructed). Absent key → no header,
            // request fails closed against the protected server (caller treats null
            // as "LLM unavailable" and falls back to the card UI).
            string? apiKey = ResolveApiKey();
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
            return null;
        }
        catch
        {
            // Network failure / server not up — caller treats null as "LLM unavailable"
            // and is expected to fall back to the card UI.
            return null;
        }
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { }
    }
}
