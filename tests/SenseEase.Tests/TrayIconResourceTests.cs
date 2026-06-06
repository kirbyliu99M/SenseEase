using SenseEase.App.Services;
using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Alpha-1 tray-ICO fix — structure-contract tests for the multi-size
/// ICO the tray now builds (<see cref="TrayIconService.GetBandIcoBytesForTesting"/>).
///
/// <para>The root-cause bug: the old wrapper hand-wrote an ICO with ONE 32×32
/// frame, so the Win11 shell had nothing to render in the 16 px tray slot but a
/// down-scaled copy of the 32 px frame — which came out blurred / blank (the
/// same PNG-at-16px bug the app icon hit). The fix ships native per-size frames.
/// These tests pin the two properties that guarantee the shell never has to
/// down-scale into the tray slot:</para>
/// <list type="bullet">
///   <item>frame count &gt; 1 (it is a genuine multi-size ICO), and</item>
///   <item>a 16×16 ICONDIRENTRY with BitCount 32 exists (a native tray-slot
///         frame the shell can pick directly).</item>
/// </list>
///
/// <para>The bytes are read via the internal hook so the assertions see the
/// EXACT bytes we wrote, not Win32's possibly-normalised <c>Icon.Save</c>
/// round-trip. <c>InternalsVisibleTo("SenseEase.Tests")</c> is already wired in
/// SenseEase.App.csproj.</para></summary>
public class TrayIconResourceTests
{
    [Theory]
    [InlineData(TierColorPalette.TierBand.VeryHigh)] // grey / calm
    [InlineData(TierColorPalette.TierBand.Medium)]   // amber + hatch
    [InlineData(TierColorPalette.TierBand.VeryLow)]  // clay + wedge
    public void BandIco_HasMultipleFrames(TierColorPalette.TierBand band)
    {
        byte[] ico = TrayIconService.GetBandIcoBytesForTesting(band);

        // ── ICONDIR (6 bytes): reserved=0, type=1 (ICO), count=N ──
        Assert.True(ico.Length >= 6, "ICO too short to hold an ICONDIR header");
        Assert.Equal(0, ico[0]); // Reserved low
        Assert.Equal(0, ico[1]); // Reserved high
        Assert.Equal(1, ico[2]); // Type = 1 (ICO)
        Assert.Equal(0, ico[3]); // Type high

        int frameCount = System.BitConverter.ToUInt16(ico, 4);
        // The whole point of the fix: more than one frame so the shell can pick
        // a native size rather than down-scaling a single 32 px frame.
        Assert.True(frameCount > 1,
            $"expected a multi-size ICO (frame count > 1) but got {frameCount}");

        // The byte stream must be large enough to hold ICONDIR + N entries.
        Assert.True(ico.Length >= 6 + frameCount * 16,
            "ICO truncated before the ICONDIRENTRY table");
    }

    [Theory]
    [InlineData(TierColorPalette.TierBand.VeryHigh)]
    [InlineData(TierColorPalette.TierBand.Medium)]
    [InlineData(TierColorPalette.TierBand.VeryLow)]
    public void BandIco_Contains16x16Frame_With32BitDepth(TierColorPalette.TierBand band)
    {
        byte[] ico = TrayIconService.GetBandIcoBytesForTesting(band);
        int frameCount = System.BitConverter.ToUInt16(ico, 4);

        bool found16 = false;
        for (int i = 0; i < frameCount; i++)
        {
            int entry = 6 + i * 16;            // ICONDIRENTRY base offset
            int width  = ico[entry + 0];       // BYTE (0 = 256)
            int height = ico[entry + 1];       // BYTE (0 = 256)
            int bitCount = System.BitConverter.ToUInt16(ico, entry + 6);

            if (width == 16 && height == 16)
            {
                found16 = true;
                // The native tray-slot frame must be true-colour 32-bit so the
                // alpha channel carries real transparency (not a paletted blur).
                Assert.Equal(32, bitCount);

                // Sanity: the entry's bytesInRes / imageOffset must point inside
                // the stream so the shell can actually read the frame.
                uint bytesInRes  = System.BitConverter.ToUInt32(ico, entry + 8);
                uint imageOffset = System.BitConverter.ToUInt32(ico, entry + 12);
                Assert.True(bytesInRes > 0, "16×16 frame has zero-length payload");
                Assert.True(imageOffset + bytesInRes <= (uint)ico.Length,
                    "16×16 frame payload runs past the end of the ICO");
            }
        }

        Assert.True(found16,
            "expected a native 16×16 ICONDIRENTRY (the tray-slot frame) but none was present");
    }
}
