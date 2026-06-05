using System;
using System.IO;
using System.Threading;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Phase 4 WGC fix (UVHS Playbook clause 5): shared diagnostic-log sink for
/// <c>%LOCALAPPDATA%\SenseEase\diag\wgc_diag.txt</c> (H51: moved out of
/// <c>%TEMP%</c>) with three guarantees the original inline
/// <c>try { AppendAllText } catch { }</c> pattern did not provide:
///
/// <list type="number">
///   <item><b>Retry on IOException</b> — concurrent writers (WGC provider,
///         sidecar lifecycle logger, tray ctor + watchdog, MainWindow load
///         instrumentation) routinely race on the file; a single sharing
///         violation no longer silently drops the log line.</item>
///   <item><b>Monotonic sequence prefix</b> <c>[N=xxxxxxx]</c> on every line.
///         A gap in N values (e.g. …N=041 → N=043) reveals a silently-lost
///         write at N=042 even after all retries failed. This is the
///         "orthogonal probe" mandated by UVHS Playbook clause 5: the
///         verification mechanism is not the same shared mutable log it
///         verifies.</item>
///   <item><b>Independent loss sink</b>
///         <c>%LOCALAPPDATA%\SenseEase\diag\wgc_diag.lost.log</c>.
///         When all retries fail, the failure event itself is recorded to a
///         separate file with relaxed concurrency expectations (only this
///         class writes there). Provides a second orthogonal probe surface
///         for Alpha-machine UVHS post-mortems.</item>
/// </list>
///
/// <para>Scope: addresses options 1 (retry) and 3 (orthogonal probe) of the
/// advisor's three-option WGC clean-up. Option 2 (single-writer queue
/// serialising every diag write through one thread) is deferred to v1.0 —
/// it is the structurally cleanest fix but requires lifecycle plumbing this
/// late-Alpha sprint does not have time to justify.</para>
///
/// <para>Process-local counter; resets per app launch. UVHS sessions only
/// need within-run continuity to detect silent loss, so persisting the
/// counter across launches would add complexity without value.</para>
/// </summary>
internal static class WgcDiagLog
{
    /// <summary>H51: diag files live under
    /// <c>%LOCALAPPDATA%\SenseEase\diag\</c> (moved out of <c>%TEMP%</c> so the
    /// usage-time fingerprint is cleaned by H25 uninstall instead of leaking
    /// across runs until 30-day %TEMP% auto-cleanup). Falls back to
    /// <c>%TEMP%</c> if LocalApplicationData cannot be resolved.</summary>
    private static readonly string DiagDir = ResolveDiagDir();

    public static readonly string DiagFile =
        Path.Combine(DiagDir, "wgc_diag.txt");

    public static readonly string LossLog =
        Path.Combine(DiagDir, "wgc_diag.lost.log");

    /// <summary>H51/H36: when <see cref="DiagFile"/> reaches this size the
    /// current file is rotated to <c>wgc_diag.prev.txt</c> (single generation)
    /// before the next append, bounding unbounded growth from a wedged sidecar
    /// emitting malformed lines at 15 Hz.</summary>
    private const long RotateThresholdBytes = 5L * 1024 * 1024;

    private static readonly string PrevFile =
        Path.Combine(DiagDir, "wgc_diag.prev.txt");

    private static long _seq;
    private static long _silentLoss;

    /// <summary>Resolve the diag directory and best-effort create it. On any
    /// failure (LocalApplicationData empty, ACL denial) falls back to
    /// <c>%TEMP%</c> so logging never throws at type-init.</summary>
    private static string ResolveDiagDir()
    {
        try
        {
            string local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                string dir = Path.Combine(local, "SenseEase", "diag");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
        catch
        {
            // Fall through to %TEMP%.
        }
        return Path.GetTempPath();
    }

    /// <summary>H51/H36: rotate <see cref="DiagFile"/> to
    /// <see cref="PrevFile"/> when it crosses <see cref="RotateThresholdBytes"/>.
    /// Single generation — the previous .prev is overwritten. Best-effort: any
    /// IO failure leaves the current file in place and the append proceeds.</summary>
    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(DiagFile);
            if (!fi.Exists || fi.Length < RotateThresholdBytes)
                return;

            // File.Move does not overwrite; delete the stale .prev first.
            if (File.Exists(PrevFile))
                File.Delete(PrevFile);
            File.Move(DiagFile, PrevFile);
        }
        catch
        {
            // Concurrent writer / sharing violation / ACL — leave as-is and
            // let the file keep growing rather than drop the append.
        }
    }

    /// <summary>Process-local count of <see cref="Append"/> calls whose
    /// retries all failed (the diagnostic line was silently lost from
    /// <see cref="DiagFile"/>). Surfaced for unit-test verification and
    /// for UVHS audit (post-session check: SilentLossCount > 0 means the
    /// shared log alone is not trustworthy for this run).</summary>
    public static long SilentLossCount => Interlocked.Read(ref _silentLoss);

    /// <summary>Append <paramref name="msg"/> to the shared diag log with
    /// orthogonal-probe sequence prefix. Retries up to 3 attempts on
    /// IOException with a 5/10 ms back-off. On permanent failure increments
    /// <see cref="SilentLossCount"/> and best-effort writes the lost line
    /// to <see cref="LossLog"/>.</summary>
    public static void Append(string msg)
    {
        long n = Interlocked.Increment(ref _seq);
        string line = $"[N={n:D7}] {msg}";
        if (!line.EndsWith("\n", StringComparison.Ordinal))
            line += Environment.NewLine;

        RotateIfNeeded();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.AppendAllText(DiagFile, line);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                // Concurrent writer holding the file; back off briefly.
                Thread.Sleep(5 * (attempt + 1));
            }
            catch
            {
                break;
            }
        }

        Interlocked.Increment(ref _silentLoss);
        try
        {
            File.AppendAllText(LossLog,
                $"[{DateTime.UtcNow:O}] LOST N={n:D7}: {msg.TrimEnd()}{Environment.NewLine}");
        }
        catch
        {
            // The loss-sink itself failed — there is no further fall-back.
            // The in-memory SilentLossCount still records that loss occurred.
        }
    }

    /// <summary>Phase 4: replaces the historical <c>File.WriteAllText</c>
    /// "=== WGC Start ===" pattern at <see cref="WgcSensingProvider.Start"/>.
    /// NO LONGER TRUNCATES — instead appends a delimiter line so the diag
    /// file's history is preserved across every WGC restart. Same orthogonal
    /// probe semantics as <see cref="Append"/>.</summary>
    public static void WriteHeader(string headerMsg)
    {
        Append("======== " + headerMsg + " ========");
    }

    /// <summary>Reset a per-session log file (e.g. the OFI validation trace)
    /// where truncation is intentional. Retries on IOException; on permanent
    /// failure records the event to <see cref="LossLog"/> so the silent
    /// truncate-or-not state is observable post-session.</summary>
    public static void ResetSessionFile(string path, string headerLine)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(path, headerLine);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(5 * (attempt + 1));
            }
            catch
            {
                break;
            }
        }

        Interlocked.Increment(ref _silentLoss);
        try
        {
            File.AppendAllText(LossLog,
                $"[{DateTime.UtcNow:O}] LOST ResetSessionFile({path}){Environment.NewLine}");
        }
        catch { }
    }
}
