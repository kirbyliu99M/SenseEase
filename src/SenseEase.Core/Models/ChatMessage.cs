namespace SenseEase.Core.Models;

public enum ChatRole { User, Assistant }

public sealed class ChatMessage
{
    public ChatRole Role    { get; init; }
    public string Content   { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
