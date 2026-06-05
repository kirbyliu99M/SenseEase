using SenseEase.App.Accessibility;
using Xunit;

namespace SenseEase.Tests;

/// <summary>C1 (UX audit): the reduced-motion probe must be robust — it gates every
/// decorative animation in the app, so it must NEVER throw and must fail open
/// (animations allowed) if the OS query is unavailable.</summary>
public class AccessibilityHelperTests
{
    [Fact]
    public void ReducedMotion_ReturnsBool_WithoutThrowing()
    {
        // Value reflects the host's OS "show animations" setting; we only assert it
        // executes and returns a value (never throws).
        bool result = AccessibilityHelper.ReducedMotion();
        Assert.True(result || !result);
    }
}
