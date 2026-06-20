using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Ultrabook plan #5 — the pure hardware classification that picks the LLM accelerator (Vulkan baseline /
/// AMD-NPU FastFlowLM / Intel-NPU OpenVINO). The live device enumeration is confirmed on real hardware
/// separately; these pin the CLASSIFICATION logic deterministically from injected device-name lists.
/// </summary>
public class HardwareProbeTests
{
    [Fact]
    public void AmdNpuDevice_PrefersAmdNpu()
    {
        var p = HardwareProbe.Classify("AuthenticAMD",
            new[] { "AMD Radeon 890M", "AMD IPU Device", "Realtek Audio" });
        Assert.True(p.HasAmdNpu);
        Assert.False(p.HasIntelNpu);
        Assert.Equal(AcceleratorKind.AmdNpu, p.Preferred);
    }

    [Fact]
    public void IntelAiBoost_PrefersIntelNpu()
    {
        var p = HardwareProbe.Classify("GenuineIntel",
            new[] { "Intel(R) Arc(TM) Graphics", "Intel(R) AI Boost", "Intel(R) Core(TM) Ultra" });
        Assert.True(p.HasIntelNpu);
        Assert.False(p.HasAmdNpu);
        Assert.Equal(AcceleratorKind.IntelNpu, p.Preferred);
    }

    [Fact]
    public void NoNpuDevices_FallsBackToVulkan()
    {
        var p = HardwareProbe.Classify("AuthenticAMD",
            new[] { "AMD Radeon Graphics", "Standard NVMe", "USB Root Hub" });
        Assert.False(p.HasAmdNpu);
        Assert.False(p.HasIntelNpu);
        Assert.Equal(AcceleratorKind.VulkanFallback, p.Preferred);
    }

    [Fact]
    public void NullDeviceList_FallsBackToVulkan()
        => Assert.Equal(AcceleratorKind.VulkanFallback, HardwareProbe.Classify("GenuineIntel", null).Preferred);

    [Fact]
    public void EmptyDeviceList_FallsBackToVulkan()
        => Assert.Equal(AcceleratorKind.VulkanFallback,
            HardwareProbe.Classify("GenuineIntel", System.Array.Empty<string>()).Preferred);

    [Fact]
    public void RealHx370_GenericNpuName_AttributedToAmdByCpuVendor()
    {
        // The exact dev-box case (2026-06-21): the NPU reports the GENERIC "NPU Compute Accelerator Device"
        // (no vendor in the name) — it must be attributed to AMD via the CPU vendor string.
        var p = HardwareProbe.Classify("AMD Ryzen AI 9 HX 370 w/ Radeon 890M",
            new[] { "AMD Radeon 890M", "NPU Compute Accelerator Device" });
        Assert.True(p.HasAmdNpu);
        Assert.False(p.HasIntelNpu);
        Assert.Equal(AcceleratorKind.AmdNpu, p.Preferred);
    }

    [Fact]
    public void SameGenericNpuName_OnIntelCpu_AttributedToIntel()
    {
        // The same generic NPU name on an Intel chip must resolve to the Intel-NPU path, NOT AMD.
        var p = HardwareProbe.Classify("Intel(R) Core(TM) Ultra 7 155H",
            new[] { "NPU Compute Accelerator Device" });
        Assert.True(p.HasIntelNpu);
        Assert.False(p.HasAmdNpu);
        Assert.Equal(AcceleratorKind.IntelNpu, p.Preferred);
    }

    [Fact]
    public void NpuPresent_UnknownCpuVendor_FallsBackToVulkan()
    {
        // An NPU we can't attribute to a known vendor → don't guess a backend; use the universal baseline.
        var p = HardwareProbe.Classify("SomeOtherVendor", new[] { "NPU Compute Accelerator Device" });
        Assert.False(p.HasAmdNpu);
        Assert.False(p.HasIntelNpu);
        Assert.Equal(AcceleratorKind.VulkanFallback, p.Preferred);
    }

    [Fact]
    public void Markers_AreCaseInsensitive()
    {
        var p = HardwareProbe.Classify("authenticamd", new[] { "amd ipu device" });
        Assert.True(p.HasAmdNpu);
    }
}
