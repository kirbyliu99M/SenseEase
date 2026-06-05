using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Nlp;

namespace SenseEase.Infrastructure.Mock;

/// <summary>
/// Keyword-based NLU fallback used when local LLM is unavailable.
/// </summary>
public sealed class MockNluService : INluService
{
    public Task<NluResult> InterpretAsync(string text)
    {
        // Alpha-1 W1 (NE7): clinical safety floor short-circuit before any
        // keyword classification, so a self-harm phrase never maps to a comfort
        // adjustment. Shares SenseEase.Core.Safety.SafetyFloor (single source).
        if (SafetyFloor.Trips(text, out string crisis))
        {
            return Task.FromResult(new NluResult
            {
                Intent = "Unknown",
                FovDelta = 0,
                Response = crisis,
            });
        }

        var (intent, fovDelta) = FeedbackLexicon.Classify(text);
        return Task.FromResult(new NluResult
        {
            Intent = intent,
            FovDelta = fovDelta,
            Response = FeedbackLexicon.BuildFallbackResponse(text, intent),
        });
    }
}
