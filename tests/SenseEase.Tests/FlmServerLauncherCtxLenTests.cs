using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>FLM context-length sizing (2026-06-28): FLM pre-allocates the whole KV cache at load, so the
/// default 32768 OOMs a 16 GB box → the model never loads and the chatbot silently drops to the lexicon.
/// <see cref="FlmServerLauncher.ChooseCtxLen"/> sizes the default to the machine's RAM (8192 on ≤16 GB,
/// 16384 above) and honours a SENSEEASE_FLM_CTX_LEN override. Pure → golden-tested here.</summary>
public class FlmServerLauncherCtxLenTests
{
    const long GB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(8L,  FlmServerLauncher.CtxLenSmallRam)]   // 8 GB  → safe floor
    [InlineData(16L, FlmServerLauncher.CtxLenSmallRam)]   // 16 GB (reports ~15.x) → safe floor
    [InlineData(24L, FlmServerLauncher.CtxLenRoomy)]      // 24 GB → roomy
    [InlineData(32L, FlmServerLauncher.CtxLenRoomy)]      // 32 GB → roomy
    [InlineData(64L, FlmServerLauncher.CtxLenRoomy)]      // 64 GB → roomy
    public void RamTiered_NoOverride(long gb, int expected)
        => Assert.Equal(expected, FlmServerLauncher.ChooseCtxLen(gb * GB, null));

    [Fact]
    public void SixteenGbBox_ReportsUnder16_GetsSafeFloor()
        // A "16 GB" machine reports ~15.6 GB usable — must still land on the safe floor, not the roomy tier.
        => Assert.Equal(FlmServerLauncher.CtxLenSmallRam,
                        FlmServerLauncher.ChooseCtxLen((long)(15.6 * GB), null));

    [Fact]
    public void UnknownRam_FailsToSafeFloor()
    {
        Assert.Equal(FlmServerLauncher.CtxLenSmallRam, FlmServerLauncher.ChooseCtxLen(0, null));
        Assert.Equal(FlmServerLauncher.CtxLenSmallRam, FlmServerLauncher.ChooseCtxLen(-1, null));
    }

    [Theory]
    [InlineData("4096", 4096)]
    [InlineData("8192", 8192)]
    [InlineData("16384", 16384)]
    [InlineData("32768", 32768)]
    public void EnvOverride_WinsOverRam(string env, int expected)
        // Even on a big box, an explicit override is honoured (lets the field drop ctx on a loaded machine).
        => Assert.Equal(expected, FlmServerLauncher.ChooseCtxLen(64 * GB, env));

    [Theory]
    [InlineData("0")]          // below the 1024 floor
    [InlineData("512")]        // below the floor
    [InlineData("999999")]     // above the 131072 ceiling
    [InlineData("")]           // blank
    [InlineData("  ")]         // whitespace
    [InlineData("garbage")]    // non-numeric
    [InlineData("16k")]        // non-numeric
    public void EnvOverride_InvalidOrOutOfRange_IgnoredFallsToRam(string env)
        // A bad override must not slip through — fall back to RAM sizing (here: 8 GB → safe floor).
        => Assert.Equal(FlmServerLauncher.CtxLenSmallRam, FlmServerLauncher.ChooseCtxLen(8 * GB, env));

    [Fact]
    public void SafeFloorMatchesVulkanCap_AndRoomyIsLarger()
    {
        Assert.Equal(8192, FlmServerLauncher.CtxLenSmallRam);   // == the Vulkan --ctx-size cap
        Assert.True(FlmServerLauncher.CtxLenRoomy > FlmServerLauncher.CtxLenSmallRam);
    }
}
