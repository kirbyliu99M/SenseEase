using System.Collections.Generic;
using System.Linq;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Ultrabook plan #5 — the accelerator SenseEase's on-device LLM should run on, chosen by a startup
/// hardware probe. The C# layer stays backend-agnostic: every backend is an OpenAI-compatible LOCAL HTTP
/// server (see <see cref="LocalLlmChatbotService"/>), so the router only decides WHICH server + model, not
/// how inference works.
/// </summary>
public enum AcceleratorKind
{
    /// <summary>Universal baseline — the bundled Vulkan <c>llama-server</c> on any iGPU/GPU/CPU (the GGUF
    /// model). The fallback for everything that isn't a matched NPU.</summary>
    VulkanFallback,

    /// <summary>AMD Ryzen-AI NPU via FastFlowLM (the Q4NX model) — ~4–5× battery vs the iGPU.</summary>
    AmdNpu,

    /// <summary>Intel CPU/iGPU/NPU via the OpenVINO <c>llama-server</c> build — the SAME GGUF, no
    /// conversion (device chosen by an env var: NPU → GPU → CPU).</summary>
    IntelNpu,
}

/// <summary>What the host hardware looks like to the backend router. Pure data; produced by
/// <see cref="HardwareProbe"/> at startup (and, with the same logic, at install time).</summary>
public sealed record HardwareProfile(string CpuVendor, bool HasAmdNpu, bool HasIntelNpu)
{
    /// <summary>The accelerator to prefer: an AMD NPU wins on AMD silicon, else an Intel NPU, else the
    /// universal Vulkan baseline. An AMD/Intel box with no usable NPU — or any non-NPU machine — lands on
    /// Vulkan, which is exactly the safe fallback.</summary>
    public AcceleratorKind Preferred =>
        HasAmdNpu ? AcceleratorKind.AmdNpu
        : HasIntelNpu ? AcceleratorKind.IntelNpu
        : AcceleratorKind.VulkanFallback;
}

/// <summary>Detects the host accelerator. The CLASSIFICATION is pure + unit-tested; the live <c>Detect</c>
/// (CPU vendor + NPU device enumeration) is wired separately so the markers can be confirmed against real
/// hardware before shipping. Never throws — any failure resolves to <see cref="AcceleratorKind.VulkanFallback"/>.</summary>
public static class HardwareProbe
{
    // Case-insensitive substrings that identify each NPU in the Windows PnP device-name list.
    // ⚠️ CONFIRM against the dev box's Device Manager string before shipping (HX 370 NPU name TBD).
    internal static readonly string[] AmdNpuMarkers   = { "amd ipu", "ipu device", "ryzen ai", "npu compute" };
    internal static readonly string[] IntelNpuMarkers = { "ai boost", "intel(r) ai boost", "intel ai boost" };

    /// <summary>Pure classification (no I/O): given the CPU-vendor string and the list of PnP device names,
    /// decide the <see cref="HardwareProfile"/>. NPU presence is a marker substring match (case-insensitive).</summary>
    internal static HardwareProfile Classify(string? cpuVendor, IEnumerable<string>? deviceNames)
    {
        var names = (deviceNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.ToLowerInvariant())
            .ToArray();
        bool amd   = names.Any(n => AmdNpuMarkers.Any(n.Contains));
        bool intel = names.Any(n => IntelNpuMarkers.Any(n.Contains));
        return new HardwareProfile((cpuVendor ?? string.Empty).Trim(), amd, intel);
    }
}
