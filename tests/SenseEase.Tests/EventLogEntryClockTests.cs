using System;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>CW-12 / UX-15: EventLogEntry's default Timestamp is UTC so it shares one
/// clock domain with the engine writers that stamp UtcNow explicitly, and the
/// human-visible Time helper converts back to local for the dashboard row.</summary>
public class EventLogEntryClockTests
{
    [Fact]
    public void DefaultTimestamp_IsUtcKind()
    {
        Assert.Equal(DateTimeKind.Utc, new EventLogEntry().Timestamp.Kind);
    }

    [Fact]
    public void Time_RendersUtcTimestampInLocalTime()
    {
        // A fixed UTC instant; the display helper must show its LOCAL clock-time.
        var utc = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        var entry = new EventLogEntry { Timestamp = utc };

        string expected = utc.ToLocalTime().ToString("HH:mm:ss");
        Assert.Equal(expected, entry.Time);
    }
}
