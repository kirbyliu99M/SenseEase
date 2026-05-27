using System;
using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 7 patch — verifies the three-scope detection
/// and the cleanup-command builder. Tests use an injected reader so they
/// don't touch real environment variables. Banner UI itself isn't unit-
/// tested (WPF dispatcher dependency; covered by the manual smoke flow
/// during the visual review).</summary>
public class ValidationModeDiagnosticsTests
{
    private const string VarName = "SENSEEASE_WGC_BAR_VALIDATION";

    // ── ScopeSnapshot ────────────────────────────────────────────────────────

    [Fact]
    public void ReadAllScopes_QueriesEachTargetExactlyOnce()
    {
        var queried = new System.Collections.Generic.List<EnvironmentVariableTarget>();
        ValidationModeDiagnostics.ReadAllScopes(VarName, (name, target) =>
        {
            Assert.Equal(VarName, name);
            queried.Add(target);
            return null;
        });
        Assert.Equal(3, queried.Count);
        Assert.Contains(EnvironmentVariableTarget.Process, queried);
        Assert.Contains(EnvironmentVariableTarget.User, queried);
        Assert.Contains(EnvironmentVariableTarget.Machine, queried);
    }

    [Fact]
    public void ScopeSnapshot_AnyScopeSet_FalseWhenAllNullOrEmpty()
    {
        Assert.False(new ValidationModeDiagnostics.ScopeSnapshot(null, null, null).AnyScopeSet);
        Assert.False(new ValidationModeDiagnostics.ScopeSnapshot("", "", "").AnyScopeSet);
        Assert.False(new ValidationModeDiagnostics.ScopeSnapshot(null, "", null).AnyScopeSet);
    }

    [Fact]
    public void ScopeSnapshot_AnyScopeSet_TrueWhenAnyHasValue()
    {
        Assert.True(new ValidationModeDiagnostics.ScopeSnapshot("1", null, null).AnyScopeSet);
        Assert.True(new ValidationModeDiagnostics.ScopeSnapshot(null, "1", null).AnyScopeSet);
        Assert.True(new ValidationModeDiagnostics.ScopeSnapshot(null, null, "1").AnyScopeSet);
    }

    [Fact]
    public void ScopeSnapshot_HasPersistentScope_TrueOnlyForUserOrMachine()
    {
        // Process-only set = NOT persistent. Closing PowerShell drops it.
        Assert.False(new ValidationModeDiagnostics.ScopeSnapshot("1", null, null).HasPersistentScope);
        // User-set = persistent.
        Assert.True(new ValidationModeDiagnostics.ScopeSnapshot(null, "1", null).HasPersistentScope);
        // Machine-set = persistent.
        Assert.True(new ValidationModeDiagnostics.ScopeSnapshot(null, null, "1").HasPersistentScope);
        // Both Process and User = persistent (User is what matters).
        Assert.True(new ValidationModeDiagnostics.ScopeSnapshot("1", "1", null).HasPersistentScope);
    }

    // ── Diagnostic text builder ──────────────────────────────────────────────

    [Fact]
    public void BuildDiagnosticText_AlwaysIncludesValidationModeHeader()
    {
        // Whatever the scope state, the header line must appear so the user
        // can grep wgc_diag.txt for "SENSEEASE VALIDATION MODE".
        var snap = new ValidationModeDiagnostics.ScopeSnapshot("1", null, null);
        var text = ValidationModeDiagnostics.BuildDiagnosticText(VarName, snap);
        Assert.Contains("SENSEEASE VALIDATION MODE", text);
        Assert.Contains("BarStub active", text);
        Assert.Contains("Production UI suppressed", text);
    }

    [Fact]
    public void BuildDiagnosticText_ListsAllThreeScopesEvenWhenSomeEmpty()
    {
        var snap = new ValidationModeDiagnostics.ScopeSnapshot("1", null, null);
        var text = ValidationModeDiagnostics.BuildDiagnosticText(VarName, snap);
        Assert.Contains("Process: 1", text);
        Assert.Contains("User:    (empty)", text);
        Assert.Contains("Machine: (empty)", text);
    }

    [Fact]
    public void BuildDiagnosticText_ProcessOnlySet_IncludesOnlyProcessCleanup()
    {
        var snap = new ValidationModeDiagnostics.ScopeSnapshot("1", null, null);
        var text = ValidationModeDiagnostics.BuildDiagnosticText(VarName, snap);
        Assert.Contains($"$env:{VarName} = $null", text);
        Assert.DoesNotContain("'User'", text);
        Assert.DoesNotContain("'Machine'", text);
    }

    [Fact]
    public void BuildDiagnosticText_UserScopeSet_IncludesUserCleanupAndReopenNote()
    {
        var snap = new ValidationModeDiagnostics.ScopeSnapshot("1", "1", null);
        var text = ValidationModeDiagnostics.BuildDiagnosticText(VarName, snap);
        Assert.Contains($"'{VarName}', $null, 'User'", text);
        Assert.Contains("re-open PowerShell", text);
    }

    [Fact]
    public void BuildDiagnosticText_MachineScopeSet_IncludesMachineCleanupAndAdminNote()
    {
        var snap = new ValidationModeDiagnostics.ScopeSnapshot("1", null, "1");
        var text = ValidationModeDiagnostics.BuildDiagnosticText(VarName, snap);
        Assert.Contains($"'{VarName}', $null, 'Machine'", text);
        Assert.Contains("Administrator", text);
    }

    [Fact]
    public void BuildDiagnosticText_AllScopesSet_IncludesAllThreeCleanupCommands()
    {
        var snap = new ValidationModeDiagnostics.ScopeSnapshot("1", "1", "1");
        var text = ValidationModeDiagnostics.BuildDiagnosticText(VarName, snap);
        Assert.Contains($"$env:{VarName} = $null", text);
        Assert.Contains("'User'", text);
        Assert.Contains("'Machine'", text);
    }
}
