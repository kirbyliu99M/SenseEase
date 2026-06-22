using System;
using System.IO;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Phase-3 AMD pack: a per-user SenseEase install (PrivilegesRequired=lowest) can write neither
/// <c>C:\Program Files\flm</c> nor the machine PATH, so the installer bundles the FastFlowLM runtime INTO the
/// app dir (<c>packs\amd-flm\flm\flm.exe</c>). <see cref="FlmServerLauncher.FindExe"/> must locate + prefer it —
/// it's the only place a bundled-FLM Beta install can be found. This pins that probe. (The PATH / Program Files
/// fallbacks are environment-dependent and validated on real AMD hardware, not unit-tested.)
/// </summary>
public sealed class FlmServerLauncherFindExeTests
{
    [Fact]
    public void FindExe_PrefersBundledFlmInAppDir()
    {
        string baseDir = AppContext.BaseDirectory;
        string bundledDir = Path.Combine(baseDir, "packs", "amd-flm", "flm");
        string bundledExe = Path.Combine(bundledDir, "flm.exe");
        bool created = false;
        try
        {
            if (!File.Exists(bundledExe))
            {
                Directory.CreateDirectory(bundledDir);
                File.WriteAllText(bundledExe, "stub");   // FindExe only checks existence — a stub suffices
                created = true;
            }

            // Bundled is probed FIRST, so it wins even on this dev box (which has a real flm on PATH).
            Assert.Equal(bundledExe, FlmServerLauncher.FindExe());
        }
        finally
        {
            if (created)
            {
                try { Directory.Delete(Path.Combine(baseDir, "packs", "amd-flm"), recursive: true); } catch { }
                try { Directory.Delete(Path.Combine(baseDir, "packs")); } catch { /* leave if non-empty */ }
            }
        }
    }
}
