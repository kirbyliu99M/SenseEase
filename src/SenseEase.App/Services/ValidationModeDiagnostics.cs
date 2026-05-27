using System;
using System.Text;

namespace SenseEase.App.Services;

/// <summary>
/// Pop-up sprint Task 7 patch: three-level environment-variable scope
/// detection + human-readable diagnostic text for the validation-mode
/// banner. Pure logic; no WPF dependencies.
///
/// <para>Why three-level: <c>$env:SENSEEASE_WGC_BAR_VALIDATION = $null</c> in
/// PowerShell only clears the PROCESS scope. If the user set the variable
/// at User or Machine scope (e.g. via System Properties → Environment
/// Variables, or <c>[Environment]::SetEnvironmentVariable(..., "User")</c>),
/// closing PowerShell and opening a new one will resurrect it. The banner
/// surfaces all three scopes so the user sees exactly which level they need
/// to clear.</para>
///
/// <para>Lives in <see cref="SenseEase.App.Services"/> next to
/// <see cref="PopupSurfaceGate"/>. The gate decides whether to spawn the
/// validation stub (Process scope only — Process inherits User+Machine on
/// launch, so a Process check is sufficient for the spawn decision). This
/// diagnostics helper reports WHERE the var is actually set so cleanup
/// instructions are accurate.</para>
/// </summary>
public static class ValidationModeDiagnostics
{
    /// <summary>Snapshot of one environment variable across the three
    /// .NET scopes. Null/empty means "not set at this scope".</summary>
    public sealed record ScopeSnapshot(
        string? ProcessValue,
        string? UserValue,
        string? MachineValue)
    {
        /// <summary>True if the variable is set at any scope.</summary>
        public bool AnyScopeSet => !string.IsNullOrEmpty(ProcessValue)
                                || !string.IsNullOrEmpty(UserValue)
                                || !string.IsNullOrEmpty(MachineValue);

        /// <summary>True if the variable is set at the ROOT level (User or
        /// Machine), meaning a process-only clear won't permanently fix it.</summary>
        public bool HasPersistentScope => !string.IsNullOrEmpty(UserValue)
                                       || !string.IsNullOrEmpty(MachineValue);
    }

    /// <summary>Reads <paramref name="variableName"/> at all three scopes.
    /// Production callers use <see cref="ReadCurrent"/>; tests can call
    /// this with a stub reader for determinism.</summary>
    public static ScopeSnapshot ReadAllScopes(
        string variableName,
        Func<string, EnvironmentVariableTarget, string?> reader)
    {
        return new ScopeSnapshot(
            ProcessValue:  reader(variableName, EnvironmentVariableTarget.Process),
            UserValue:     reader(variableName, EnvironmentVariableTarget.User),
            MachineValue:  reader(variableName, EnvironmentVariableTarget.Machine));
    }

    /// <summary>Convenience overload for production paths — reads from the
    /// real <see cref="Environment.GetEnvironmentVariable(string, EnvironmentVariableTarget)"/>.</summary>
    public static ScopeSnapshot ReadCurrent(string variableName)
        => ReadAllScopes(variableName, Environment.GetEnvironmentVariable);

    /// <summary>Builds the human-readable diagnostic block shown in the
    /// banner + appended to <c>wgc_diag.txt</c>. Includes scope-by-scope
    /// values and the exact PowerShell cleanup command for each non-null
    /// scope. Returned string is multi-line (newline-separated); the banner
    /// renders it in a monospace TextBlock for alignment.</summary>
    public static string BuildDiagnosticText(string variableName, ScopeSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"⚠ SENSEEASE VALIDATION MODE");
        sb.AppendLine($"BarStub active. Production UI suppressed.");
        sb.AppendLine();
        sb.AppendLine($"Env var {variableName} set at:");
        sb.AppendLine($"  Process: {FormatScope(snap.ProcessValue)}");
        sb.AppendLine($"  User:    {FormatScope(snap.UserValue)}");
        sb.AppendLine($"  Machine: {FormatScope(snap.MachineValue)}");
        sb.AppendLine();
        sb.AppendLine("To exit validation mode:");
        if (!string.IsNullOrEmpty(snap.ProcessValue))
        {
            sb.AppendLine($"  $env:{variableName} = $null");
        }
        if (!string.IsNullOrEmpty(snap.UserValue))
        {
            sb.AppendLine($"  [Environment]::SetEnvironmentVariable('{variableName}', $null, 'User')");
            sb.AppendLine("    (then re-open PowerShell for it to take effect)");
        }
        if (!string.IsNullOrEmpty(snap.MachineValue))
        {
            sb.AppendLine($"  [Environment]::SetEnvironmentVariable('{variableName}', $null, 'Machine')");
            sb.AppendLine("    (run as Administrator; then re-open PowerShell)");
        }
        return sb.ToString();
    }

    private static string FormatScope(string? value)
        => string.IsNullOrEmpty(value) ? "(empty)" : value;
}
