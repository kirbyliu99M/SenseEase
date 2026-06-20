using System.IO;
using System.Threading.Tasks;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-1 Wave 1 (C3 / H8): the self-harm safety floor is now a single source
/// (<see cref="SafetyFloor"/>) that both chatbot services call, so the vocabulary
/// can no longer drift between them — the C3 duplicate-source-drift pattern that
/// had previously diverged a single colon. These tests prove a phrase the OLD
/// per-service tables MISSED (Simplified Han) now trips end-to-end through the
/// service, and that Traditional/Simplified pairs are treated identically.
/// </summary>
public class SafetyFloorParityTests
{
    [Theory]
    [InlineData("自殺", "自杀")]
    [InlineData("自殘", "自残")]
    [InlineData("跳樓", "跳楼")]
    public void TraditionalAndSimplified_ProduceIdenticalCrisisResponse(string traditional, string simplified)
    {
        Assert.True(SafetyFloor.Trips(traditional, out string a));
        Assert.True(SafetyFloor.Trips(simplified, out string b));
        Assert.Equal(a, b);
        Assert.Equal(SafetyFloor.CrisisResponseZh, a);
    }

    [Fact]
    public async Task MistralService_TripsOnSimplifiedHan_RoutesThroughSharedFloor()
    {
        // "我想自杀" is Simplified — the pre-extraction Ordinal table (Traditional
        // only) would NOT have tripped. Routing through the shared SafetyFloor
        // (H3) means the SDK service short-circuits to the crisis line and the
        // model is never loaded. NOTE (2026-06-21): the crisis-line LANGUAGE now
        // follows the UI-language toggle (read live inside SendAsync), so assert it
        // tripped to EITHER localized crisis line — not a specific script — to stay
        // deterministic regardless of the ambient toggle in a parallel run.
        string fakePath = Path.Combine(Path.GetTempPath(),
            $"no-model-{System.Guid.NewGuid():N}.gguf");
        using var service = new LocalLlmChatbotService(fakePath);

        var result = await service.SendAsync("我想自杀");

        Assert.True(result.Response == SafetyFloor.CrisisResponseZh
                 || result.Response == SafetyFloor.CrisisResponseEn,
            "simplified-Han self-harm must short-circuit to a localized crisis line");
        Assert.Contains("findahelpline", result.Response);   // the international line is in both
        Assert.Equal(0, result.FovDelta);
        Assert.False(service.IsModelLoaded);
    }
}
