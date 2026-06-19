using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Alpha-2 W6 (H9): the llama-server readiness gate parses GET /props and reports the
/// model loaded only when the body carries a non-empty model identifier
/// (model_path / model / default_generation_settings.model). This pins that gate — the
/// plain /health 200-OK check it replaced could report "ready" with no model actually
/// loaded (a loopback that answered before the GGUF finished mapping).
///
/// <para>Note: the H9 spec also floated an exact model-name suffix match
/// ("...Ministral-3-3B-Instruct-2512"); the shipped gate deliberately accepts ANY
/// non-empty identifier to avoid brittleness against the real GGUF filename (which
/// carries a quantization suffix + .gguf extension and would not "end with" that exact
/// string). These tests pin the SHIPPED behaviour, plus the parse-hardening (MaxDepth,
/// non-object / malformed / empty-string rejection).</para>
/// </summary>
public class LocalLlmChatbotServiceHardeningTests
{
    [Theory]
    [InlineData("{\"model_path\":\"C:/models/Ministral-3B-Instruct-2512-Q4_K_M.gguf\"}")]
    [InlineData("{\"model\":\"ministral-3b\"}")]
    [InlineData("{\"default_generation_settings\":{\"model\":\"ministral-3b\"}}")]
    [InlineData("{\"default_generation_settings\":{\"model_path\":\"/m/x.gguf\"},\"other\":1}")]
    public void PropsReportModelLoaded_NonEmptyModel_ReportsLoaded(string body)
        => Assert.True(LocalLlmChatbotService.PropsReportModelLoaded(body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]                                    // valid JSON but not an object
    [InlineData("{}")]                                    // object, no model field
    [InlineData("{\"model_path\":\"\"}")]                 // present but empty string
    [InlineData("{\"model\":\"   \"}")]                   // whitespace-only
    [InlineData("{\"default_generation_settings\":{}}")]  // nested object, no model
    public void PropsReportModelLoaded_MissingOrEmptyModel_ReportsNotLoaded(string? body)
        => Assert.False(LocalLlmChatbotService.PropsReportModelLoaded(body));
}
