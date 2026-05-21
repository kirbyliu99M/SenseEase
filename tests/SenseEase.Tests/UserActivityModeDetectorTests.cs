using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Pure-static `DetectMode` is the testable kernel of the auto Driver/Passenger
/// detector. Verifies the asymmetric-hysteresis threshold logic without needing
/// the timer, P/Invoke, or any threading.
/// </summary>
public class UserActivityModeDetectorTests
{
    [Theory]
    [InlineData(0, "Passenger", "Driver")]       // Idle = 0 → recent input → Driver
    [InlineData(2000, "Passenger", "Driver")]    // Just inside Driver threshold (2500)
    [InlineData(2500, "Passenger", "Driver")]    // Exactly at boundary → still Driver
    [InlineData(2501, "Passenger", "Passenger")] // Just past → no transition (hysteresis)
    [InlineData(4999, "Passenger", "Passenger")] // Still in hysteresis band
    [InlineData(5000, "Passenger", "Passenger")] // Boundary — needs ≥ to switch (test next)
    public void Passenger_StaysOrEntersDriverOnly_BelowThreshold(int idleMs, string current, string expected)
    {
        Assert.Equal(expected, UserActivityModeDetector.DetectMode(idleMs, current));
    }

    [Theory]
    [InlineData(0, "Driver", "Driver")]          // Active → stays Driver
    [InlineData(2500, "Driver", "Driver")]
    [InlineData(4999, "Driver", "Driver")]       // Still in hysteresis band → hold Driver
    [InlineData(5000, "Driver", "Passenger")]    // Reached Passenger threshold → switch
    [InlineData(60000, "Driver", "Passenger")]   // Long idle
    public void Driver_TransitionsToPassenger_AboveThreshold(int idleMs, string current, string expected)
    {
        Assert.Equal(expected, UserActivityModeDetector.DetectMode(idleMs, current));
    }

    [Fact]
    public void DetectMode_IsCaseInsensitive()
    {
        Assert.Equal("Driver", UserActivityModeDetector.DetectMode(0, "passenger"));
        Assert.Equal("Driver", UserActivityModeDetector.DetectMode(0, "PASSENGER"));
        Assert.Equal("Passenger", UserActivityModeDetector.DetectMode(5000, "driver"));
    }
}
