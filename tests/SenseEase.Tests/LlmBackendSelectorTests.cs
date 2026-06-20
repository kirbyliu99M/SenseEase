using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Ultrabook plan #5 — the RUNTIME decision to try the FastFlowLM NPU backend for the active model. Pure gate;
/// the live launch (reuse-an-running-server / else `flm serve`) is validated on real AMD hardware separately.
/// </summary>
public class LlmBackendSelectorTests
{
    [Fact]
    public void AmdCpu_WithNpuTag_FlmInstalled_NoOverride_TriesNpu()
        => Assert.True(LlmBackendSelector.ShouldTryFlmNpu("qwen3-it:4b",
            "AMD64 Family 26 Model 36 Stepping 0, AuthenticAMD", flmInstalled: true, externalOverrideSet: false));

    [Fact]
    public void RyzenBrandString_AlsoCountsAsAmd()
        => Assert.True(LlmBackendSelector.ShouldTryFlmNpu("qwen3-it:4b",
            "AMD Ryzen AI 9 HX 370 w/ Radeon 890M", flmInstalled: true, externalOverrideSet: false));

    [Fact]
    public void IntelCpu_DoesNotTryFlm()   // FastFlowLM/Ryzen-AI is AMD-only; Intel takes the OpenVINO path later
        => Assert.False(LlmBackendSelector.ShouldTryFlmNpu("qwen3-it:4b",
            "Intel64 Family 6 Model 170 Stepping 4, GenuineIntel", flmInstalled: true, externalOverrideSet: false));

    [Fact]
    public void NoPairedNpuTag_DoesNotTryFlm()
        => Assert.False(LlmBackendSelector.ShouldTryFlmNpu(null,
            "AuthenticAMD", flmInstalled: true, externalOverrideSet: false));

    [Fact]
    public void FlmNotInstalled_DoesNotTryFlm()
        => Assert.False(LlmBackendSelector.ShouldTryFlmNpu("qwen3-it:4b",
            "AuthenticAMD", flmInstalled: false, externalOverrideSet: false));

    [Fact]
    public void ExplicitExternalOverride_Wins_SoWeDoNotAlsoManageNpu()
        => Assert.False(LlmBackendSelector.ShouldTryFlmNpu("qwen3-it:4b",
            "AuthenticAMD", flmInstalled: true, externalOverrideSet: true));

    [Fact]
    public void EmptyVendor_DoesNotTryFlm()
        => Assert.False(LlmBackendSelector.ShouldTryFlmNpu("qwen3-it:4b",
            "", flmInstalled: true, externalOverrideSet: false));

    // ── OpenVINO (Intel) selection ───────────────────────────────────────────────────────────────────

    [Fact]
    public void IntelCpu_WithOpenVinoServer_NoOverride_UsesOpenVino()
        => Assert.True(LlmBackendSelector.ShouldUseOpenVino(
            "Intel64 Family 6 Model 170 Stepping 4, GenuineIntel", openVinoServerPresent: true, externalOverrideSet: false));

    [Fact]
    public void AmdCpu_DoesNotUseOpenVino()   // AMD takes the FastFlowLM path instead
        => Assert.False(LlmBackendSelector.ShouldUseOpenVino(
            "AMD64 Family 26 Model 36 Stepping 0, AuthenticAMD", openVinoServerPresent: true, externalOverrideSet: false));

    [Fact]
    public void IntelCpu_WithoutOpenVinoServerBundled_FallsBackToVulkan()
        => Assert.False(LlmBackendSelector.ShouldUseOpenVino(
            "GenuineIntel", openVinoServerPresent: false, externalOverrideSet: false));

    [Fact]
    public void ExternalOverride_DisablesOpenVino()
        => Assert.False(LlmBackendSelector.ShouldUseOpenVino(
            "GenuineIntel", openVinoServerPresent: true, externalOverrideSet: true));
}
