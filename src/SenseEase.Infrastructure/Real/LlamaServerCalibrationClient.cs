using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Real;

// ─────────────────────────────────────────────────────────────────────────────
// Production ICalibrationLlmClient — posts to the running llama-server's
// OpenAI-compatible /v1/chat/completions on http://127.0.0.1:8181. Mirrors
// MistralChatbotService's HTTP pattern (private HttpClient with a long
// timeout, JSON deserialization, choices[0].message.content extraction).
//
// Does NOT spawn a server — only talks to one MistralChatbotService already
// brought up. When the server isn't running, calls return null and the
// caller (CalibrationChatbotService) treats it as "LLM unavailable" and
// surfaces fallback to the UI layer.
//
// Extracted from CalibrationChatbotService.cs (Stage 4 review): the HTTP
// client is plumbing, not service logic, and lives more cleanly in its
// own file. Same namespace + lifetime as the service so a single using
// import covers both at construction sites.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LlamaServerCalibrationClient : ICalibrationLlmClient, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const int ServerPort = 8181;
    private string ServerUrl => $"http://127.0.0.1:{ServerPort}";

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
            using var resp = await _http.PostAsJsonAsync(
                $"{ServerUrl}/v1/chat/completions", requestBody, ct).ConfigureAwait(false);
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
