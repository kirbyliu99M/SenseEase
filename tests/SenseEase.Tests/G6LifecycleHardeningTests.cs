using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Wave B1a / G6 — app lifecycle + service hardening pins (EXIT-02, EXIT-05, SEC-01,
/// SEC-06, PKG-06, and the B0 model_trace data-root seam).
///
/// <para><b>Testability choice.</b> The lifecycle changes live inside
/// <c>App.OnStartup</c> / <c>App.LogCrash</c>, which cannot be unit-driven without a
/// live WPF <c>Application</c> (a per-AppDomain singleton already owned by the smoke
/// tests). So — following the established "drive the pure seam where one exists, and
/// pin the rest with a read-the-.cs structural lint" idiom (see
/// <c>UserModelAnalysisKickoffTests</c> / <c>BannedPhraseLintTests</c>) — the
/// behaviour that IS reachable (the <see cref="SenseEaseDataRoot"/> provider seam) is
/// exercised directly, and the source-only edits (hook ordering, the dropped
/// <c>--api-key</c> CLI arg, the env-var injection, the redactor swap, the sweep call)
/// are pinned by asserting on the committed source text.</para>
/// </summary>
public class G6LifecycleHardeningTests
{
    // ── B0 seam: SenseEaseDataRoot provider (runtime-testable) ─────────────────

    [Fact]
    public void SenseEaseDataRoot_NoProvider_FallsBackToLocalAppData()
    {
        var saved = SenseEaseDataRoot.Provider;
        try
        {
            SenseEaseDataRoot.Provider = null;
            Assert.Equal(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SenseEaseDataRoot.Resolve());
        }
        finally { SenseEaseDataRoot.Provider = saved; }
    }

    [Fact]
    public void SenseEaseDataRoot_WiredProvider_ResolvesToProviderRoot()
    {
        var saved = SenseEaseDataRoot.Provider;
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "g6-dataroot-" + Guid.NewGuid().ToString("N"));
            SenseEaseDataRoot.Provider = () => root;
            Assert.Equal(root, SenseEaseDataRoot.Resolve());
        }
        finally { SenseEaseDataRoot.Provider = saved; }
    }

    [Fact]
    public void SenseEaseDataRoot_ProviderThrowsOrEmpty_FallsBackToLocalAppData()
    {
        var saved = SenseEaseDataRoot.Provider;
        string osRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            // A throwing provider must NOT propagate — Resolve falls back to the OS folder.
            SenseEaseDataRoot.Provider = () => throw new InvalidOperationException("boom");
            Assert.Equal(osRoot, SenseEaseDataRoot.Resolve());

            // An empty/whitespace root is treated as "unset" → same fallback.
            SenseEaseDataRoot.Provider = () => string.Empty;
            Assert.Equal(osRoot, SenseEaseDataRoot.Resolve());
        }
        finally { SenseEaseDataRoot.Provider = saved; }
    }

    // ── SEC-06: API key via child environment, dropped from the command line ───

    [Fact]
    public void SEC06_ApiKey_NotOnCommandLine_AndInjectedViaEnvironment()
    {
        string src = ReadSource("src", "SenseEase.Infrastructure", "Real", "LocalLlmChatbotService.cs");

        // The Bearer key must NOT be interpolated into serverArgs / the command line —
        // same-user processes can read Win32_Process.CommandLine (H9 threat model).
        Assert.DoesNotContain("--api-key {_apiKey}", src);
        Assert.DoesNotContain("--api-key {", src);

        // It must be delivered through the child's private environment instead, using
        // llama.cpp's LLAMA_ARG_API_KEY convention.
        Assert.Contains("LLAMA_ARG_API_KEY", src);
        Assert.Matches(
            @"psi\.EnvironmentVariables\[""LLAMA_ARG_API_KEY""\]\s*=\s*_apiKey",
            src);
    }

    // ── SEC-01: PathRedactor is the single source of truth ─────────────────────

    [Fact]
    public void SEC01_App_DelegatesRedactionToPathRedactor_NotAnInlineRegex()
    {
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");

        // The crash-log writers must call the shared PathRedactor...
        Assert.Contains("PathRedactor.Redact(", src);
        // ...and must NOT carry their own duplicate "\Users\..." redaction regex any more.
        Assert.DoesNotContain("private static readonly Regex UserPathRedaction", src);
    }

    [Fact]
    public void SEC01_StderrPump_RedactsEachLine()
    {
        string src = ReadSource("src", "SenseEase.Infrastructure", "Real", "LocalLlmChatbotService.cs");
        // The stderr pump that backs llama-server.log must redact before writing.
        Assert.Matches(@"writer\.WriteLine\(\s*PathRedactor\.Redact\(line\)\s*\)", src);
    }

    // ── EXIT-02: crash/lifecycle hooks are the FIRST statements of OnStartup ────

    [Fact]
    public void EXIT02_HooksRegisteredBeforeMutexIntegrityAndPrefs()
    {
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");

        int methodStart = src.IndexOf("protected override void OnStartup", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "OnStartup not found.");

        int dispatcherHook = src.IndexOf("DispatcherUnhandledException +=", methodStart, StringComparison.Ordinal);
        int appDomainHook   = src.IndexOf("AppDomain.CurrentDomain.UnhandledException +=", methodStart, StringComparison.Ordinal);
        int mutexCreate     = src.IndexOf("_singleInstanceMutex = new Mutex", methodStart, StringComparison.Ordinal);
        int integrityVerify = src.IndexOf("IntegrityVerifier.Verify(", methodStart, StringComparison.Ordinal);
        int prefsHydrate    = src.IndexOf("UserPreferences.Current.HeadTrackingEnabled", methodStart, StringComparison.Ordinal);

        Assert.True(dispatcherHook >= 0, "DispatcherUnhandledException hook missing in OnStartup.");
        Assert.True(appDomainHook >= 0, "AppDomain.UnhandledException hook missing in OnStartup.");

        // The hooks must precede the mutex / integrity / prefs work so a throw anywhere
        // in that startup work still records a crash.log entry (EXIT-02).
        Assert.True(dispatcherHook < mutexCreate, "Dispatcher hook must precede mutex creation.");
        Assert.True(appDomainHook < mutexCreate, "AppDomain hook must precede mutex creation.");
        Assert.True(dispatcherHook < integrityVerify, "Hooks must precede the integrity gate.");
        Assert.True(dispatcherHook < prefsHydrate, "Hooks must precede prefs hydration.");
    }

    // ── EXIT-05: LogCrash IOException retry + crash-flush hook calls ────────────

    [Fact]
    public void EXIT05_LogCrash_HasBoundedIoExceptionRetry()
    {
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");

        Match method = Regex.Match(
            src,
            @"private static void LogCrash\([^)]*\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        Assert.True(method.Success, "Could not isolate LogCrash body.");
        string body = method.Groups["body"].Value;

        // A retry loop guarded by a typed IOException catch with a bounded attempt cap.
        Assert.Contains("AppendAllText", body);
        Assert.Matches(@"catch\s*\(\s*IOException\s*\)\s*when\s*\(\s*attempt\s*<", body);
    }

    [Fact]
    public void EXIT05_CrashHandlers_BestEffortFlushBehaviorTail()
    {
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");

        // A static-safe accessor exists that reaches the G2 crash-flush hook on the VM...
        Assert.Contains("TryFlushBehaviorAggregateForCrash()", src);
        Assert.Contains("TryFlushBehaviorForCrash", src);

        // ...and it is invoked from the DispatcherUnhandledException handler and the
        // IsTerminating AppDomain branch (best-effort behaviour-tail flush, EXIT-05).
        int dispatcher = src.IndexOf("DispatcherUnhandledException +=", StringComparison.Ordinal);
        int terminating = src.IndexOf("if (args.IsTerminating)", StringComparison.Ordinal);
        Assert.True(dispatcher >= 0 && terminating >= 0);

        int flushAfterDispatcher = src.IndexOf("TryFlushBehaviorForCrash()", dispatcher, StringComparison.Ordinal);
        int flushAfterTerminating = src.IndexOf("TryFlushBehaviorForCrash()", terminating, StringComparison.Ordinal);
        Assert.True(flushAfterDispatcher >= 0, "Dispatcher handler must flush the behaviour tail.");
        Assert.True(flushAfterTerminating >= 0, "IsTerminating branch must flush the behaviour tail.");
    }

    // ── PKG-06: background manifest sweep, diagnostic-only this wave ────────────

    [Fact]
    public void PKG06_StartupSweep_RunsOnBackgroundTask_AfterTheTwoExeChecks()
    {
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");

        int exeVerify = src.IndexOf("IntegrityVerifier.Verify(", StringComparison.Ordinal);
        int sweepCall = src.IndexOf("IntegrityVerifier.VerifyManifestSweep(", StringComparison.Ordinal);
        Assert.True(exeVerify >= 0, "The synchronous two-exe integrity checks must exist.");
        Assert.True(sweepCall >= 0, "The PKG-06 manifest sweep call must exist.");
        Assert.True(exeVerify < sweepCall, "The sweep must come AFTER the two-exe integrity checks.");

        // It runs on a background task (must not hash ~350 MB on the UI thread)...
        int taskRun = src.LastIndexOf("Task.Run(", sweepCall, StringComparison.Ordinal);
        Assert.True(taskRun >= 0, "The sweep must run on a background Task.Run.");

        // ...and the sweep's failure details are redacted before reaching wgc_diag.
        Assert.Matches(@"PathRedactor\.Redact\(\s*entry\.Detail\s*\)", src);
    }

    [Fact]
    public void PKG06_SweepDoesNotRaiseTheReinstallSuppression()
    {
        // The 'please reinstall' degrade stays gated to the two synchronous exe checks
        // (it sets IntegrityFailed). The diagnostic sweep block must NOT set that flag —
        // it is diagnostic-only this wave (Alpha-1 posture). Assert the sweep block does
        // not flip IntegrityFailed.
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");

        int sweepCall = src.IndexOf("IntegrityVerifier.VerifyManifestSweep(", StringComparison.Ordinal);
        Assert.True(sweepCall >= 0);
        // From the sweep call to the end of its enclosing Task.Run lambda there must be
        // no IntegrityFailed assignment. Bound the window generously at the next method.
        int windowEnd = src.IndexOf("/// <summary>CW-4", sweepCall, StringComparison.Ordinal);
        if (windowEnd < 0) windowEnd = src.Length;
        string window = src.Substring(sweepCall, windowEnd - sweepCall);
        Assert.DoesNotContain("IntegrityFailed = true", window);
    }

    // ── B0 deferred: model_trace routes through the seam, host wires it ────────

    [Fact]
    public void B0_ModelTracePath_RoutesThroughDataRootSeam()
    {
        string src = ReadSource("src", "SenseEase.Infrastructure", "Real", "LocalLlmChatbotService.cs");
        // The trace path must resolve through SenseEaseDataRoot (so the test override
        // isolates it), NOT a hardcoded Environment.GetFolderPath at the trace site.
        Assert.Matches(@"_modelTracePath\s*=>\s*Path\.Combine\(\s*SenseEaseDataRoot\.Resolve\(\)", src);
    }

    [Fact]
    public void B0_App_WiresSenseEaseDataRootProvider()
    {
        string src = ReadSource("src", "SenseEase.App", "App.xaml.cs");
        Assert.Matches(
            @"SenseEaseDataRoot\.Provider\s*=\s*\n?\s*\(\)\s*=>\s*Configuration\.AppConfig\.DataRoot",
            src);
    }

    // ── Seam still exists for the kickoff regression sibling (sanity) ──────────

    [Fact]
    public void SenseEaseDataRoot_IsPublicWithLambdaProvider()
    {
        // Mirrors ChatbotUiLanguage: a public static cross-boundary holder so the App
        // can push the data-root down without Infrastructure referencing App.
        Type t = typeof(SenseEaseDataRoot);
        Assert.True(t.IsAbstract && t.IsSealed, "SenseEaseDataRoot should be a static class.");
        PropertyInfo? provider = t.GetProperty("Provider", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(provider);
        Assert.Equal(typeof(Func<string>), provider!.PropertyType);
        MethodInfo? resolve = t.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(resolve);
        Assert.Equal(typeof(string), resolve!.ReturnType);
    }

    // ── source locator (walk up from the test bin to the repo root) ────────────

    private static string ReadSource(params string[] relativeParts)
    {
        string relative = Path.Combine(relativeParts);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate " + relative + " from " + AppContext.BaseDirectory);
    }
}
