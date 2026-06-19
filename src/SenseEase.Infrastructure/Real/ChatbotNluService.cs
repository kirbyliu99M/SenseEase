using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Nlp;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// INluService adapter backed by LocalLlmChatbotService with robust keyword fallback.
/// </summary>
public sealed class ChatbotNluService : INluService, IDisposable
{
    private readonly LocalLlmChatbotService _chatbot;
    private readonly bool _ownsChatbot;

    public ChatbotNluService(string modelPath)
    {
        _chatbot = new LocalLlmChatbotService(modelPath);
        _ownsChatbot = true;
    }

    public ChatbotNluService(LocalLlmChatbotService chatbot)
    {
        _chatbot = chatbot;
        _ownsChatbot = false;
    }

    public async Task<NluResult> InterpretAsync(string text)
    {
        try
        {
            return await _chatbot.SendAsync(text ?? string.Empty).ConfigureAwait(false);
        }
        catch
        {
            // Alpha-1 W1 (NE7): if the LLM path threw, the deterministic fallback
            // must still honour the safety floor before keyword classification
            // (the try-path SendAsync already runs it; this covers the catch).
            if (SafetyFloor.Trips(text, out string crisis))
            {
                return new NluResult { Intent = "Unknown", FovDelta = 0, Response = crisis };
            }

            var (intent, fovDelta) = FeedbackLexicon.Classify(text);
            return new NluResult
            {
                Intent = intent,
                FovDelta = fovDelta,
                Response = FeedbackLexicon.BuildFallbackResponse(text, intent),
            };
        }
    }

    public void Dispose()
    {
        if (_ownsChatbot) _chatbot.Dispose();
    }
}
