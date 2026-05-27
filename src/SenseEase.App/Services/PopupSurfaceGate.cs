namespace SenseEase.App.Services;

/// <summary>
/// Pop-up sprint Task 7: centralises the decision logic that gates which
/// UI surfaces spawn at app start. Two surfaces are mutually exclusive
/// with the WGC bar-validation harness; one surface is also gated by the
/// emergency-rollback feature flag.
///
/// <para>The rules (truth table):</para>
/// <list type="table">
///   <listheader>
///     <term>VALIDATION env var</term><term>UseNewBarOverlay</term>
///     <term>BarStub</term><term>Persistent bar + tray</term>
///   </listheader>
///   <item>
///     <term>"1"</term><term>true</term><term>yes</term><term>no</term>
///   </item>
///   <item>
///     <term>"1"</term><term>false</term><term>yes</term><term>no</term>
///   </item>
///   <item>
///     <term>unset / other</term><term>true</term><term>no</term><term>yes</term>
///   </item>
///   <item>
///     <term>unset / other</term><term>false</term><term>no</term><term>no (legacy mode)</term>
///   </item>
/// </list>
///
/// <para>Lives in a separate file so the gating is headless-testable
/// without dragging in MainWindow's WPF dependencies.</para>
/// </summary>
public static class PopupSurfaceGate
{
    /// <summary>Validation env var literal — exposed so tests and the
    /// MainWindow caller refer to the same string.</summary>
    public const string ValidationEnvVar = "SENSEEASE_WGC_BAR_VALIDATION";

    /// <summary>True iff <c>SENSEEASE_WGC_BAR_VALIDATION=1</c> at app
    /// launch. The validation harness reads this once at start; toggling
    /// it mid-session requires a restart.</summary>
    public static bool ShouldSpawnValidationStub(string? envVarValue)
        => string.Equals(envVarValue, "1", System.StringComparison.Ordinal);

    /// <summary>True iff the production bar + tray should spawn. False
    /// either because the validation harness is active (mutex) OR because
    /// the user has rolled back to legacy mode (UseNewBarOverlay=false).
    /// Bar and tray share the rule — they spawn together or not at all.</summary>
    public static bool ShouldSpawnProductionBarAndTray(string? envVarValue, bool useNewBarOverlay)
        => !ShouldSpawnValidationStub(envVarValue) && useNewBarOverlay;
}
