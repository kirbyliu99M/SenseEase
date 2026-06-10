using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// CW-4 regression pin (FEAT-01/EXIT-01/TEST-07): the UM4 retrospective analysis
/// must actually run. Two failures combined to make it a permanent no-op:
/// <list type="number">
///   <item>App.xaml uses StartupUri, so <c>Application.MainWindow</c> was null at
///   the end of OnStartup — the inline
///   <c>(MainWindow?.DataContext as MainViewModel)?.StartBackgroundUserModelAnalysis()</c>
///   never fired (CW-4 part 1).</item>
///   <item>The single-shot T+2min attempt usually ran before any chat lazy-spawned
///   llama-server, found no LLM, and gave up forever (CW-4 part 2).</item>
/// </list>
///
/// <para><b>Testability choice.</b> The full <c>MainViewModel</c> cannot be
/// unit-constructed without <c>Application.Current</c> + engine wiring (see
/// <c>D6DayOneProtectionTests</c> / <c>PauseResumeTests</c> headers), and a real WPF
/// <c>Application</c> is a per-AppDomain singleton already owned by
/// <c>MainWindowSmokeTests</c> — so we do NOT spin one here. Instead, following the
/// established "mirror/lock the pure decision + pin the wiring seam" idiom:
/// </para>
/// <list type="bullet">
///   <item>Part 2 is pinned in two halves: the extracted pure retry decision
///   <see cref="MainViewModel.ShouldRetryUserModelAnalysis"/> is driven directly (no
///   timers) — first failure ⇒ retry, success ⇒ stop, available-but-incomplete ⇒ retry —
///   AND, because the decision is only a tautology unless the loop can actually REACH the
///   available-but-incomplete state, the faulted-RunAsync reachability is pinned by a
///   source-text structural lint (<see cref="FaultedRunAsync_IsCaughtInsideRetryLoop"/>):
///   a non-cancellation fault must be caught inside the loop, not escape it.</item>
///   <item>Part 1 is pinned by reflecting the App-level wiring seam: the one-shot
///   <c>App.OnFirstActivatedStartUserModelAnalysis(object?, EventArgs)</c> handler must
///   exist. Reverting to the end-of-OnStartup null-MainWindow inline call deletes that
///   handler and fails <see cref="App_DeclaresOneShotActivatedKickoffSeam"/>.</item>
/// </list>
/// </summary>
public class UserModelAnalysisKickoffTests
{
    // ── Part 2: VM-level retry decision (pure, no real timer) ──────────────────

    [Fact]
    public void LlmUnavailable_SchedulesRetry()
    {
        // First pass before any chat spawns llama-server: LLM is not up → must retry.
        Assert.True(MainViewModel.ShouldRetryUserModelAnalysis(llmAvailable: false, ranToCompletion: false));
    }

    [Fact]
    public void LlmAvailableAndCompleted_StopsRetrying()
    {
        // One attempt ran to completion with the LLM up → loop is done for the session.
        Assert.False(MainViewModel.ShouldRetryUserModelAnalysis(llmAvailable: true, ranToCompletion: true));
    }

    [Fact]
    public void LlmAvailableButIncomplete_DecisionIsRetry()
    {
        // The decision rule alone: LLM up + run not completed ⇒ retry. NOTE this only
        // pins the pure function; on its own it is a tautology for the faulted-RunAsync
        // case, because the loop only ever PRODUCES the (true, false) state if a faulted
        // RunAsync is caught INSIDE the loop. That reachability is pinned separately by
        // FaultedRunAsync_IsCaughtInsideRetryLoop below.
        Assert.True(MainViewModel.ShouldRetryUserModelAnalysis(llmAvailable: true, ranToCompletion: false));
    }

    [Fact]
    public void FaultedRunAsync_IsCaughtInsideRetryLoop()
    {
        // Reachability pin for the (true, false) state. A non-cancellation fault from
        // RunAsync (e.g. an AV file-lock at index load/save) MUST be caught inside the
        // while-loop so the loop reaches ShouldRetryUserModelAnalysis(true, false) and
        // retries — NOT propagate to the task's outer catch, which would end the session's
        // retry permanently. If RunAsync is awaited bare (no inner try), the analysis loop
        // silently dies on the first transient fault. Source-text structural lint, mirroring
        // BannedPhraseLintTests' read-the-.cs idiom (the loop can't be unit-driven: it needs
        // Application.Current + real Task.Delay timers — see this class's header).
        string src = File.ReadAllText(FindMainViewModelSource());

        // Isolate StartBackgroundUserModelAnalysis's body so we assert about THIS loop only.
        Match method = Regex.Match(
            src,
            @"void\s+StartBackgroundUserModelAnalysis\s*\(\s*\)(?<body>.*?)\n    /// <summary>UM4 retry decision",
            RegexOptions.Singleline);
        Assert.True(method.Success, "Could not locate StartBackgroundUserModelAnalysis body in MainViewModel.cs.");
        string body = method.Groups["body"].Value;

        int runAt = body.IndexOf("RunAsync(ct)", StringComparison.Ordinal);
        Assert.True(runAt >= 0, "RunAsync(ct) call not found in StartBackgroundUserModelAnalysis.");

        // The await must be guarded: an inner try precedes it and the matching catch block
        // re-throws OperationCanceledException (cancellation breaks the loop) while a bare
        // catch swallows any other fault (so ranToCompletion stays false → loop retries).
        string before = body.Substring(0, runAt);
        int innerTry = before.LastIndexOf("try", StringComparison.Ordinal);
        Assert.True(innerTry >= 0, "RunAsync(ct) must be awaited inside an inner try (it currently is not).");

        string after = body.Substring(runAt);
        Assert.Matches(@"catch\s*\(\s*OperationCanceledException\s*\)\s*\{\s*throw\s*;", after);
        // A bare catch (no exception type) immediately follows, swallowing transient faults.
        Assert.Matches(@"catch\s*\{", after);
    }

    // ── Part 1: App-level one-shot Activated wiring seam (regression pin) ───────

    [Fact]
    public void App_DeclaresOneShotActivatedKickoffSeam()
    {
        // The fix moves the kickoff off the null-MainWindow end-of-OnStartup line into a
        // one-shot Application.Activated handler (which fires after StartupUri creates the
        // window, so MainWindow.DataContext is the VM). This method IS that seam; its
        // absence means a regression back to the inert inline pattern.
        MethodInfo? seam = typeof(SenseEase.App.App).GetMethod(
            "OnFirstActivatedStartUserModelAnalysis",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(seam);
        ParameterInfo[] ps = seam!.GetParameters();
        Assert.Equal(2, ps.Length);
        Assert.Equal(typeof(object), ps[0].ParameterType);
        Assert.Equal(typeof(EventArgs), ps[1].ParameterType);
    }

    // Walk up from the test bin to the repo and resolve the VM source (mirrors
    // BannedPhraseLintTests.FindAppDir's walk-up-from-BaseDirectory idiom).
    private static string FindMainViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(
                dir.FullName, "src", "SenseEase.App", "ViewModels", "MainViewModel.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate src/SenseEase.App/ViewModels/MainViewModel.cs from " + AppContext.BaseDirectory);
    }
}
