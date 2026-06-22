using System;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Warm-on-load gating (2026-06-22, Option C): after a successful LOCAL load the service fires one
/// max_tokens=1 completion to compile the Vulkan shaders + seed the ~4,500-token system-prefix KV cache, so
/// the user's first real turn isn't a cold prefill. That one-time prefill is SKIPPED on an ultrabook that is
/// BOTH on battery AND in power-save, and on external/FLM backends (which don't prefix-reuse). The actual
/// HTTP warmup needs a real server (manual integration check); these pin the GATING decision deterministically
/// via the injected predicate — no real server. Mirrors LocalLlmChatbotServiceIdleUnloadTests' *ForTest seam.
/// </summary>
public class LocalLlmChatbotServiceWarmupTests
{
    // A path that does not exist → IsModelAvailable is false, so no real llama-server is ever spawned.
    private const string NoModel = "C:/nonexistent/senseease-test-model.gguf";

    [Fact]
    public void Warm_Allowed_ByDefault_WhenNoPredicate()
    {
        using var svc = new LocalLlmChatbotService(NoModel);
        Assert.True(svc.WarmOnLoadAllowedForTest());          // null predicate ⇒ always warm (plugged-in default)
    }

    [Fact]
    public void Warm_Allowed_WhenPredicateReportsFalse()
    {
        using var svc = new LocalLlmChatbotService(NoModel, suppressWarmup: () => false);
        Assert.True(svc.WarmOnLoadAllowedForTest());          // not (battery AND power-save) ⇒ warm
    }

    [Fact]
    public void Warm_Skipped_WhenPredicateReportsTrue()       // battery AND power-save
    {
        using var svc = new LocalLlmChatbotService(NoModel, suppressWarmup: () => true);
        Assert.False(svc.WarmOnLoadAllowedForTest());         // constrained ultrabook ⇒ decline the cold prefill
    }

    [Fact]
    public void Warm_Skipped_OnExternalBackend_EvenWhenPredicateAllows()
    {
        // FLM / external NPU servers don't prefix-reuse, so there is nothing to seed — never warm them,
        // regardless of the power predicate.
        using var svc = new LocalLlmChatbotService(NoModel,
            externalBaseUrl: "http://127.0.0.1:59999", suppressWarmup: () => false);
        Assert.False(svc.WarmOnLoadAllowedForTest());
    }

    [Fact]
    public void Predicate_IsQueriedLive_NotSnapshotAtCtor()
    {
        bool onBatteryAndPowerSave = false;
        using var svc = new LocalLlmChatbotService(NoModel, suppressWarmup: () => onBatteryAndPowerSave);
        Assert.True(svc.WarmOnLoadAllowedForTest());           // plugged-in at load ⇒ warm
        onBatteryAndPowerSave = true;                          // user unplugs + enters power-save
        Assert.False(svc.WarmOnLoadAllowedForTest());          // re-queried live ⇒ next reload skips
    }
}
