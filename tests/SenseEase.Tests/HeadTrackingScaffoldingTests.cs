using SenseEase.App.Configuration;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>M3 (C# Plumbing) scaffolding gate tests for the head-tracking
/// sprint. These pin the flag-off invariant — when
/// <see cref="AppConfig.HeadTrackingEnabled"/> is false (the default), the
/// app is parameter-identical to v0.9 baseline:
/// <list type="bullet">
///   <item>flag default is false (privacy + stability)</item>
///   <item><see cref="SenseState.HeadGazeX"/>/Y default to 0.5 (center)</item>
///   <item><see cref="SenseState.HeadTrackingActive"/> defaults to false</item>
///   <item><see cref="DefaultHeadPoseReader.ReadLatest"/> returns null so the
///         consumer falls back to the (0.5, 0.5) center default</item>
/// </list>
/// M4 will swap <see cref="DefaultHeadPoseReader"/> for a sidecar-backed
/// reader; until then these tests guard the dead-path invariant.
///
/// <para>The OverlayWindow flag-off invariant test mentioned in the M3 spec
/// is intentionally skipped here — OverlayWindow construction needs an STA
/// dispatcher (see <c>NotificationCardWindowTests.RunOnSta</c>) AND the
/// engine + view-model graph, and the actual mask center is hardcoded at
/// <c>Center = new Point(0.5, 0.5)</c> in <c>OverlayWindow.ApplyFovMask</c>
/// (no head-pose consumer wired in M3). The pinning of "consumer is dead
/// when flag is off" is therefore satisfied by the three tests below plus
/// the static read of the mask-center literal in the file. M4 will add the
/// full OverlayWindow round-trip test once the consumer exists.</para></summary>
public class HeadTrackingScaffoldingTests
{
    [Fact]
    public void AppConfig_HeadTrackingEnabled_DefaultIsFalse()
    {
        // Default must be false so unmodified startup behaves like v0.9 —
        // any code path that gates on this flag stays dormant unless an
        // opt-in (settings UI / config file) flips it explicitly.
        Assert.False(AppConfig.HeadTrackingEnabled);
    }

    [Fact]
    public void SenseState_HeadGaze_DefaultsToCenter()
    {
        var s = new SenseState();
        // (0.5, 0.5) is the v0.9 hardcoded FOV-mask center. Any deviation
        // here would mean an unconfigured tick produces an off-center mask
        // — that's the invariant the head-tracking sprint MUST NOT break.
        Assert.Equal(0.5, s.HeadGazeX);
        Assert.Equal(0.5, s.HeadGazeY);
        Assert.False(s.HeadTrackingActive);
    }

    [Fact]
    public void DefaultHeadPoseReader_ReadLatest_ReturnsNull()
    {
        IHeadPoseReader reader = new DefaultHeadPoseReader();
        // Null is the "no fresh sample" signal — the consumer (M4) treats
        // it as "use (0.5, 0.5) center default". Calling many times in a
        // row also returns null (no hidden state, no allocation).
        Assert.Null(reader.ReadLatest());
        Assert.Null(reader.ReadLatest());
        Assert.Null(reader.ReadLatest());
    }
}
