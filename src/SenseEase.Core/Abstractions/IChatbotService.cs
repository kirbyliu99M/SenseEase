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
}
