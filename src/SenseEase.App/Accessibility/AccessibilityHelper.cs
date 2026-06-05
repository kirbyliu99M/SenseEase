using System;
using System.Runtime.InteropServices;

namespace SenseEase.App.Accessibility;

/// <summary>
/// C1 (UX audit): the single read of the OS "show animations" setting
/// (<c>SPI_GETCLIENTAREAANIMATION</c> — Settings → Accessibility → Visual effects →
/// Animation effects, or the legacy "Show animations in Windows").
///
/// <para>SenseEase exists to REDUCE visually-induced motion sickness, so it must
/// honor Reduce Motion: every DECORATIVE chrome animation (bar hover grow, card
/// fade-in/idle-fade, typing dots, view transitions) is gated on
/// <see cref="ReducedMotion"/> and snaps to its target instead of animating when
/// the user has asked for less motion. The CLINICAL FOV-mask cross-fade is
/// deliberately EXEMPT — it is the protection itself, not decoration (per the
/// locked motion hierarchy).</para>
/// </summary>
public static class AccessibilityHelper
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    // BOOL is a 4-byte int in Win32; marshal as int (NOT bool) to avoid a
    // 1-vs-4-byte mismatch that would read garbage.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    /// <summary>True when the user has minimized animations at the OS level —
    /// decorative storyboards must then snap to their end value instead of
    /// animating. Fails OPEN (returns false / animations allowed) if the query
    /// fails, so a probe error never suppresses motion the user didn't ask to cut.</summary>
    public static bool ReducedMotion()
    {
        try
        {
            int animationsEnabled = 1; // default: animations on
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref animationsEnabled, 0))
                return animationsEnabled == 0;
        }
        catch
        {
            // P/Invoke unavailable (non-Windows test host, etc.) — fail open.
        }
        return false;
    }
}
