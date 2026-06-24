using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// #38c — the OV GenAI host launcher's DISCOVERY + graceful-null contract: it locates the host exe + IR model
/// via the bundled Intel pack (<c>packs\intel-ov\...</c>) or a dev/field env override, and returns null (→ the
/// Vulkan fallback) when either is absent. The live spawn + cold-compile readiness is validated on real Intel
/// hardware, not unit-tested. Env-var mutation is confined to this (sequential) class.
/// </summary>
public sealed class OvGenAiHostLauncherTests
{
    [Fact]
    public void FindExe_PrefersEnvOverride()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "ovhost-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(tmp, "stub");                  // FindExe only checks existence
        try
        {
            Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_EXE", tmp);
            Assert.Equal(tmp, OvGenAiHostLauncher.FindExe());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_EXE", null);
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void FindExe_BundledPackInAppDir()
    {
        Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_EXE", null);   // no override masking the probe
        string baseDir = AppContext.BaseDirectory;
        string bundledDir = Path.Combine(baseDir, "packs", "intel-ov", "host");
        string bundledExe = Path.Combine(bundledDir, "genai_host.exe");
        bool created = false;
        try
        {
            if (!File.Exists(bundledExe))
            {
                Directory.CreateDirectory(bundledDir);
                File.WriteAllText(bundledExe, "stub");
                created = true;
            }
            Assert.Equal(bundledExe, OvGenAiHostLauncher.FindExe());
        }
        finally
        {
            if (created)
            {
                try { Directory.Delete(Path.Combine(baseDir, "packs", "intel-ov"), recursive: true); } catch { }
                try { Directory.Delete(Path.Combine(baseDir, "packs")); } catch { /* leave if non-empty */ }
            }
        }
    }

    [Fact]
    public void FindModelDir_PrefersEnvOverride()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "ovmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_MODEL", tmp);
            Assert.Equal(tmp, OvGenAiHostLauncher.FindModelDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_MODEL", null);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TryAcquire_NoExeOrModel_ReturnsNull_NoThrow()
    {
        // No env override + no bundled pack (dev box) → null, so the caller falls back to Vulkan. Port 59999 has
        // no listener, so the reuse-probe fails fast and we exercise the missing-exe/model path. Must not throw.
        Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_EXE", null);
        Environment.SetEnvironmentVariable("SENSEEASE_OVHOST_MODEL", null);
        ManagedBackend? mb = await OvGenAiHostLauncher.TryAcquireAsync(
            OvGenAiHostLauncher.DefaultModelId, device: "NPU", port: 59999, trace: null, ct: CancellationToken.None);
        Assert.Null(mb);
    }
}
