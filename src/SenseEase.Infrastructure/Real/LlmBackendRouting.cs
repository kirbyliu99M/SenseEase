using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // The NPU appears under the Microsoft "ComputeAccelerator" device class with a GENERIC name — on the
    // HX 370 it's "NPU Compute Accelerator Device" (no vendor in the string), and an Intel NPU reports the
    // same generic name. So NPU PRESENCE is matched by these markers, and the CPU VENDOR decides whose NPU
    // it is (the NPU is on the CPU die, so NPU-vendor == CPU-vendor). Confirmed on the HX 370, 2026-06-21.
    internal static readonly string[] NpuMarkers =
        { "npu compute accelerator", "npu compute", "ipu device", "amd ipu", "ai boost", "neural processor" };

    /// <summary>Pure classification (no I/O): given the CPU-vendor string and the list of PnP device names,
    /// decide the <see cref="HardwareProfile"/>. An NPU device (generic marker match) is attributed to the
    /// CPU vendor; an unknown vendor or no NPU lands on the Vulkan fallback.</summary>
    internal static HardwareProfile Classify(string? cpuVendor, IEnumerable<string>? deviceNames)
    {
        string vendor = (cpuVendor ?? string.Empty).Trim();
        string v = vendor.ToLowerInvariant();
        bool isAmd   = v.Contains("amd") || v.Contains("ryzen");    // "AuthenticAMD" / "AMD Ryzen AI 9 HX 370…"
        bool isIntel = v.Contains("intel");                         // "GenuineIntel" / "Intel(R) Core(TM) Ultra…"
        bool npuPresent = (deviceNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.ToLowerInvariant())
            .Any(n => NpuMarkers.Any(n.Contains));
        return new HardwareProfile(vendor, HasAmdNpu: isAmd && npuPresent, HasIntelNpu: isIntel && npuPresent);
    }

    /// <summary>The CPU vendor/brand string for the RUNTIME backend decision — read from the env var
    /// <c>PROCESSOR_IDENTIFIER</c> (e.g. "AMD64 Family 26 … AuthenticAMD" / "… GenuineIntel"). Dependency-free
    /// and non-throwing; empty when unavailable. Only "amd"/"intel" matter, not the marketing name. (Live NPU
    /// presence is proven by a successful FastFlowLM launch, so no PnP device enumeration is needed here.)</summary>
    public static string DetectCpuVendor()
        => (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty).Trim();
}

/// <summary>A local OpenAI backend the chatbot can drive: its base URL + model tag, plus the process we OWN
/// (launched ourselves) — null when we reused an already-running server, so the chatbot never kills a server
/// it didn't start. Produced by <see cref="FlmServerLauncher"/>.</summary>
public sealed record ManagedBackend(string BaseUrl, string ModelTag, Process? OwnedProcess);

/// <summary>Which bundled llama-server build the LOCAL path launches. Both are llama.cpp servers — same GGUF,
/// grammar, and bearer key — differing only in compute backend + device env, so the chatbot client code is
/// identical for both.</summary>
public enum LocalServerKind
{
    /// <summary>The universal Vulkan build (any iGPU/GPU/CPU) under runtimes/.../vulkan/. The default.</summary>
    Vulkan,
    /// <summary>The Intel OpenVINO build under runtimes/.../openvino/ — runs the SAME GGUF on an Intel
    /// NPU/iGPU/CPU, device chosen by GGML_OPENVINO_DEVICE (we request NPU; OpenVINO auto-falls-back to CPU if
    /// the NPU is unavailable). Bundled only on Intel-targeted installs.</summary>
    OpenVino,
}

/// <summary>The RUNTIME backend decision (ultrabook #5): does this machine try the FastFlowLM NPU backend for
/// the active model? Pure + unit-tested; the live inputs (CPU vendor, flm presence, env override) are gathered
/// by the caller. The decision is deliberately optimistic — the actual NPU/serve capability is verified by the
/// launch attempt, which falls back to Vulkan on failure — so we never enumerate devices here.</summary>
public static class LlmBackendSelector
{
    /// <summary>True only when: the model has a paired NPU tag, the CPU is AMD (FastFlowLM/Ryzen-AI is the one
    /// NPU runtime we bundle today), FastFlowLM is installed, and no explicit external override is set (a dev
    /// <c>SENSEEASE_LLM_BASEURL</c> wins and is handled directly by the service).</summary>
    public static bool ShouldTryFlmNpu(string? npuModelTag, string? cpuVendor, bool flmInstalled, bool externalOverrideSet)
    {
        if (externalOverrideSet) return false;
        if (string.IsNullOrWhiteSpace(npuModelTag)) return false;
        if (!flmInstalled) return false;
        string v = (cpuVendor ?? string.Empty).ToLowerInvariant();
        return v.Contains("amd") || v.Contains("ryzen");
    }

    /// <summary>Whether to launch the Intel OpenVINO local server for the active model: an Intel CPU, the
    /// OpenVINO build actually present (bundled only on Intel-targeted installs), and no explicit external
    /// override. AMD takes the FastFlowLM path (<see cref="ShouldTryFlmNpu"/>); everything else stays on the
    /// universal Vulkan build. NPU presence isn't probed here — OpenVINO selects NPU→CPU itself at launch.</summary>
    public static bool ShouldUseOpenVino(string? cpuVendor, bool openVinoServerPresent, bool externalOverrideSet)
    {
        if (externalOverrideSet) return false;
        if (!openVinoServerPresent) return false;
        return (cpuVendor ?? string.Empty).ToLowerInvariant().Contains("intel");
    }
}
