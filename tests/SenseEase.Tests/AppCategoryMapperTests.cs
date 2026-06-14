using System;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 D-16 / theme-2: the foreground-app category mapper + key formatting (the pure,
/// privacy-minimised join key). The Win32 resolution itself needs a live window so isn't
/// unit-tested here beyond the null-handle guard.
/// </summary>
public sealed class AppCategoryMapperTests
{
    [Theory]
    [InlineData("chrome", AppCategoryMapper.Browser)]
    [InlineData("msedge.exe", AppCategoryMapper.Browser)]   // .exe suffix tolerated
    [InlineData("VLC", AppCategoryMapper.Video)]            // case-insensitive
    [InlineData("potplayermini64", AppCategoryMapper.Video)]
    [InlineData("steam", AppCategoryMapper.Game)]
    [InlineData("winword", AppCategoryMapper.Office)]
    [InlineData("devenv", AppCategoryMapper.Dev)]
    [InlineData("code", AppCategoryMapper.Dev)]
    [InlineData("teams", AppCategoryMapper.Comm)]
    [InlineData("some_unknown_app_xyz", AppCategoryMapper.Other)]
    public void Categorize_MapsKnownProcesses_UnknownToOther(string proc, string expected)
        => Assert.Equal(expected, AppCategoryMapper.Categorize(proc));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Categorize_NullOrBlank_IsOther(string? proc)
        => Assert.Equal(AppCategoryMapper.Other, AppCategoryMapper.Categorize(proc));

    [Fact]
    public void FormatKey_IsCategorySlashApp()
        => Assert.Equal("video/vlc", ForegroundAppResolver.FormatKey("video", "vlc"));

    [Fact]
    public void Resolve_NullHandle_ReturnsNull()
        => Assert.Null(ForegroundAppResolver.Resolve(IntPtr.Zero));
}
