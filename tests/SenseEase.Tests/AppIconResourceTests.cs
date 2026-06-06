using System;
using System.IO;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Regression guard for the application icon. The shipped AppIcon.ico
/// must be a TRUE multi-size icon: a single 256x256 (PNG-only) frame renders
/// blank/generic in the taskbar, Alt-Tab, and the title bar because the shell
/// cannot down-scale a lone large frame for the small render slots. This pins
/// >1 ICONDIRENTRY and the presence of a 16x16 frame so the single-frame
/// regression (the original "app icon is broken" defect) cannot return.</summary>
public sealed class AppIconResourceTests
{
    [Fact]
    public void AppIcon_IsMultiSize_WithSmall16pxFrame()
    {
        string ico = LocateAppIcon();
        byte[] b = File.ReadAllBytes(ico);

        // ICONDIR header: reserved(2)=0, type(2)=1 (icon), count(2).
        Assert.True(b.Length > 6, "AppIcon.ico is truncated or empty.");
        Assert.Equal(1, BitConverter.ToUInt16(b, 2)); // type 1 == icon
        ushort count = BitConverter.ToUInt16(b, 4);
        Assert.True(count > 1,
            $"AppIcon.ico has {count} frame(s); a single-frame .ico renders blank at small " +
            "shell sizes. Regenerate as multi-size (16/24/32/48/64/256).");

        bool has16 = false;
        for (int i = 0; i < count; i++)
        {
            int off = 6 + i * 16;                 // ICONDIRENTRY is 16 bytes
            int w = b[off] == 0 ? 256 : b[off];   // width/height of 0 encodes 256
            int h = b[off + 1] == 0 ? 256 : b[off + 1];
            if (w == 16 && h == 16) has16 = true;
        }
        Assert.True(has16,
            "AppIcon.ico is missing a 16x16 frame (needed for the taskbar / title-bar / Alt-Tab small renders).");
    }

    /// <summary>Walks up from the test assembly's base directory to the repo root
    /// (identified by the solution file) and returns the source AppIcon.ico path.</summary>
    private static string LocateAppIcon()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SenseEase.Windows.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "Could not locate the repo root (SenseEase.Windows.sln) from the test base directory.");
        string ico = Path.Combine(dir!.FullName, "src", "SenseEase.App", "Resources", "AppIcon.ico");
        Assert.True(File.Exists(ico), $"AppIcon.ico not found at {ico}");
        return ico;
    }
}
