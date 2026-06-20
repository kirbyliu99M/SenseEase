using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

public interface IChatbotService : IDisposable
{
    bool IsModelAvailable { get; }
    bool IsModelLoaded    { get; }
    IReadOnlyList<ChatMessage> History { get; }

    Task LoadAsync();
    Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default);
    void ClearHistory();

    /// <summary>H24 (PII): delete any on-disk diagnostic trace this service wrote
    /// that can carry the user's chat text (e.g. the model-trace JSONL). Called from
    /// <c>ChatbotViewModel.Clear</c> after <see cref="ClearHistory"/> so "clear chat"
    /// also wipes the on-disk trace, not just the in-memory transcript. Best-effort:
    /// never throws. Default is a no-op for services that keep no on-disk trace
    /// (lexicon-only / test stubs).</summary>
    void ClearOnDiskTrace() { }

    /// <summary>W-B NEW-B14 (2026-06-20): a lightweight, history-free, grammar-free SHORT completion used
    /// ONLY for a held-turn rest advice — the host has already decided to advise rest + hold all actuation,
    /// so this must NOT run the NLU pipeline or resend the conversation transcript (no context-window
    /// growth). Returns warm, non-actuating advice text, or null when unavailable (the host then falls back
    /// to a fixed honest line). Default = unavailable, so lexicon-only services + test stubs fall straight
    /// back without a network call.</summary>
    Task<string?> CompleteShortAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
