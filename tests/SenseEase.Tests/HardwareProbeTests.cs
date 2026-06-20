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
    public void BothNpusPresent_AmdWins()
    {
        // Defensive: if a device list somehow reports both, AMD-NPU takes precedence (single, deterministic pick).
        var p = HardwareProbe.Classify("AuthenticAMD", new[] { "AMD IPU Device", "Intel(R) AI Boost" });
        Assert.True(p.HasAmdNpu);
        Assert.True(p.HasIntelNpu);
        Assert.Equal(AcceleratorKind.AmdNpu, p.Preferred);
    }

    [Fact]
    public void Markers_AreCaseInsensitive()
    {
        var p = HardwareProbe.Classify("AuthenticAMD", new[] { "amd ipu device" });
        Assert.True(p.HasAmdNpu);
    }
}
