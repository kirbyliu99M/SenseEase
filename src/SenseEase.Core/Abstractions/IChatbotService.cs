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
}
