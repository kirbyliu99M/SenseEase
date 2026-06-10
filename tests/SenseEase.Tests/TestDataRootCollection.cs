using Xunit;

namespace SenseEase.Tests;

/// <summary>CW-9: serialises the test classes that RETARGET the process-wide
/// <see cref="SenseEase.App.Configuration.AppConfig.DataRootOverride"/> at a
/// per-class temp subdir (via <see cref="TestDataRootFixture.UseClassSubdir"/>).
///
/// <para>The assembly module initializer (<see cref="TestDataRootFixture.Init"/>)
/// already isolates the WHOLE suite off the developer's real %LOCALAPPDATA% by
/// seeding an assembly-wide temp root. These specific classes go one step further
/// and point the override at their OWN subdir so they don't read each other's
/// chat_history.jsonl / user-preferences.json. Because the override is a single
/// static, two of these classes mutating it in PARALLEL would let one observe the
/// other's subdir (the documented "1 failing in full run" flake). Tagging them all
/// into ONE xUnit collection makes them run sequentially relative to each other —
/// exactly one of them owns the override at a time — without disabling parallelism
/// for the rest of the suite. Read-only consumers of the override (EaseViewTests /
/// MainWindowSmokeTests / LivenessContractTests) never collide: whatever subdir is
/// current, it is always a temp dir, never the real user data.</para></summary>
[CollectionDefinition("TestDataRoot")]
public sealed class TestDataRootCollection { }
