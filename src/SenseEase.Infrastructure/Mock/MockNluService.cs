using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Nlp;

namespace SenseEase.Infrastructure.Mock;

/// <summary>
/// Keyword-based NLU fallback used when local LLM is unavailable.
/// </summary>
public sealed class MockNluService : INluService
{
    public Task<NluResult> InterpretAsync(string text)
    {
        var (intent, fovDelta) = FeedbackLexicon.Classify(text);
        return Task.FromResult(new NluResult
        {
            Intent = intent,
            FovDelta = fovDelta,
            Response = FeedbackLexicon.BuildFallbackResponse(text, intent),
        });
    }
}
