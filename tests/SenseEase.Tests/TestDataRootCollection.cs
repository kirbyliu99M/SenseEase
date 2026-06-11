using Xunit;

namespace SenseEase.Tests;

/// <summary>CW-9 + UX-04/05/21: serialises the test classes that mutate any
/// process-wide singleton that is shared across the whole test run.
///
/// <para>Two singletons are covered:</para>
/// <list type="bullet">
///   <item><see cref="SenseEase.App.Configuration.AppConfig.DataRootOverride"/> —
///         retargeted at a per-class temp subdir by the classes that read/write
///         user-preferences.json, chat_history.jsonl, etc., via
///         <see cref="TestDataRootFixture.UseClassSubdir"/>.</item>
///   <item><see cref="SenseEase.App.Localization.LocalizationManager.Current"/>
///         <c>.Language</c> — mutated by tests that assert language-sensitive
///         output (zh/en proposal summaries, tray-menu labels, status copy). A
///         race between two classes toggling the language in opposite directions
///         causes non-deterministic assertion failures (the documented
///         "1 failing in full run" flake observed 2026-06-11).</item>
/// </list>
/// <para>The assembly module initializer (<see cref="TestDataRootFixture.Init"/>)
/// already isolates the WHOLE suite off the developer's real %LOCALAPPDATA% by
/// seeding an assembly-wide temp root. Classes that also belong here go one step
/// further and point the data-root override at their OWN subdir so they don't read
/// each other's files. Because both overrides are single statics, two classes
/// mutating them in PARALLEL would produce flaky failures. Tagging them all into
/// ONE xUnit collection makes them run sequentially relative to each other —
/// exactly one of them owns the overrides at a time — without disabling parallelism
/// for the rest of the suite. Read-only consumers (EaseViewTests /
/// MainWindowSmokeTests / LivenessContractTests) never collide: they always read
/// from a temp dir, never the real user data.</para></summary>
[CollectionDefinition("TestDataRoot")]
public sealed class TestDataRootCollection { }
