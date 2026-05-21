using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Nlp;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// INluService adapter backed by MistralChatbotService with robust keyword fallback.
/// </summary>
public sealed class ChatbotNluService : INluService, IDisposable
{
    private readonly MistralChatbotService _chatbot;
    private readonly bool _ownsChatbot;

    public ChatbotNluService(string modelPath)
    {
        _chatbot = new MistralChatbotService(modelPath);
        _ownsChatbot = true;
    }

    public ChatbotNluService(MistralChatbotService chatbot)
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
