using System.IO;
using System.Threading.Tasks;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// M0 (v0.8.1 follow-up): contract tests for the self-harm safety floor
/// migrated from CalibrationChatbotService into MistralChatbotService.
///
/// <para>Two layers tested:</para>
/// <list type="number">
///   <item>The pure <c>TripsSafetyFloor</c> phrase-detector — direct static
///         method call covering zh + en + negative + empty cases.</item>
///   <item>The <c>SendAsync</c> short-circuit — service constructed with a
///         non-existent model path (so <c>IsModelAvailable=false</c>); the
///         safety phrase should return the crisis response without ever
///         attempting to load the model OR invoking the lexicon fallback.</item>
/// </list>
///
/// <para>The full HTTP/llama-server round-trip is NOT exercised here —
/// requires a live local server. These tests pin the safety contract
/// which must hold regardless of model state.</para>
/// </summary>
public class MistralChatbotServiceSafetyFloorTests
{
    // ── Phrase-detector unit tests ─────────────────────────────────────────

    [Theory]
    [InlineData("我想死了")]
    [InlineData("不想活了，每天都好痛苦")]
    [InlineData("想去死算了")]
    [InlineData("自殺")]
    [InlineData("我要跳樓")]
    public void TripsSafetyFloor_ZhPhrase_ReturnsTrueAndCrisisResponse(string text)
    {
        bool tripped = MistralChatbotService.TripsSafetyFloor(text, out string response);
        Assert.True(tripped);
        Assert.Contains("1925", response);              // TW crisis line
        Assert.Contains("findahelpline", response);     // international
        Assert.Contains("SenseEase", response);         // explicit non-medical disclaimer
    }

    [Theory]
    [InlineData("I want to die")]
    [InlineData("I really want to kill myself")]
    [InlineData("Going to end it all")]
    [InlineData("wanna die")]
    [InlineData("self-harm has been on my mind")]
    public void TripsSafetyFloor_EnPhrase_ReturnsTrueAndCrisisResponse(string text)
    {
        bool tripped = MistralChatbotService.TripsSafetyFloor(text, out string response);
        Assert.True(tripped);
        Assert.Contains("988", response);               // US crisis line
        Assert.Contains("findahelpline", response);
    }

    [Theory]
    [InlineData("I have a headache")]
    [InlineData("我有點頭暈")]
    [InlineData("the screen is killing me")]   // "killing me" idiom — NOT in phrase list
    [InlineData("好多了，謝謝")]
    [InlineData("hi there")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public void TripsSafetyFloor_NonSafetyText_ReturnsFalse(string? text)
    {
        bool tripped = MistralChatbotService.TripsSafetyFloor(text ?? string.Empty, out string response);
        Assert.False(tripped);
        Assert.Empty(response);
    }

    [Fact]
    public void TripsSafetyFloor_EnPhraseInZhContext_UsesZhCrisisResponse()
    {
        // Mixed-language input: an English self-harm phrase with surrounding
        // Han chars triggers the en branch (lower.Contains matches), but
        // the response is the zh crisis text because IsHan(userText) is true.
        bool tripped = MistralChatbotService.TripsSafetyFloor(
            "今天真的 want to die 了", out string response);
        Assert.True(tripped);
        Assert.Contains("1925", response);       // zh response (TW)
        Assert.DoesNotContain("988", response);  // not the en response
    }

    // ── SendAsync short-circuit integration test ───────────────────────────

    [Fact]
    public async Task SendAsync_SafetyPhrase_ShortCircuitsBeforeModelLoad()
    {
        // Construct service with a model path that does not exist on disk.
        // IsModelAvailable will be false; LoadAsync is normally skipped in
        // that state (the production path falls back to FeedbackLexicon).
        // Our safety-floor check sits BEFORE that fallback — so the crisis
        // response should be returned and the lexicon path should never
        // touch the user's distress text.
        string nonExistentPath = Path.Combine(
            Path.GetTempPath(),
            $"definitely-not-a-real-gguf-{System.Guid.NewGuid():N}.gguf");

        using var service = new MistralChatbotService(nonExistentPath);
        Assert.False(service.IsModelAvailable);
        Assert.False(service.IsModelLoaded);

        var result = await service.SendAsync("我想死了");

        Assert.Contains("1925", result.Response);
        Assert.Contains("findahelpline", result.Response);
        Assert.Equal(0, result.FovDelta);
        Assert.Equal(0.0, result.MaskDepthDelta);
        Assert.Equal(0.0, result.GridVisibilityDelta);
        Assert.Equal(0.0, result.FeatherWidthDelta);

        // The user message + crisis response should both appear in history.
        Assert.Equal(2, service.History.Count);
        Assert.Equal("我想死了", service.History[0].Content);
        Assert.Equal(result.Response, service.History[1].Content);

        // Model remained unloaded throughout — safety floor short-circuited
        // BEFORE the load attempt.
        Assert.False(service.IsModelLoaded);
        Assert.Null(service.LoadError);
    }

    [Fact]
    public async Task SendAsync_NonSafetyMessage_DoesNotEmitCrisisResponse()
    {
        // Negative case: a normal discomfort message should NOT be short-
        // circuited. With no model available the response will come from
        // FeedbackLexicon — but it MUST NOT be the crisis text.
        string nonExistentPath = Path.Combine(
            Path.GetTempPath(),
            $"no-model-{System.Guid.NewGuid():N}.gguf");

        using var service = new MistralChatbotService(nonExistentPath);
        var result = await service.SendAsync("我有點頭暈");

        Assert.DoesNotContain("1925", result.Response);
        Assert.DoesNotContain("findahelpline", result.Response);
    }
}
