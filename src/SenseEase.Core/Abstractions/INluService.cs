using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Feedback layer NLU contract. MVP: keyword matching.
/// Future real implementation: local Mistral 7B inference (out of Phase-1 scope).
/// </summary>
public interface INluService
{
    /// <summary>Interpret a free-text user feedback utterance.</summary>
    Task<NluResult> InterpretAsync(string text);
}
