namespace SenseEase.Core.Models;

// Note: System role intentionally not modeled. Internal protocol messages
// (e.g. convergence reminders injected at turn 8 by the active chatbot service)
// use the User role with a "[SYSTEM REMINDER]" prefix to keep this enum aligned
// with llama-server's OpenAI-compatible chat-completions API, which only
// accepts user/assistant in its messages array. Adding a System enum member
// would mean every callsite that maps ChatRole -> wire string has to handle
// a value the wire protocol doesn't accept. The inline-marker pattern keeps
// the impedance mismatch contained to the one component that needs it.
public enum ChatRole { User, Assistant }

public sealed class ChatMessage
{
    public ChatRole Role    { get; init; }
    public string Content   { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
