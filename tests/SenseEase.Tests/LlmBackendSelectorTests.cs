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
}
