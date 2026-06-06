using System;
using System.IO;
using System.Security;
using SenseEase.Infrastructure.Real;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Alpha-1 engine-integrity guard: rejects file paths that escape an expected
/// base directory or that traverse a reparse point (NTFS symlink / junction /
/// mount point). The threat model is a tampered or attacker-planted path under
/// the SenseEase profile dir being redirected (via a junction) to write outside
/// the sandbox, or a crafted relative path climbing out of the base with
/// <c>..</c> segments.
///
/// <para><b>Lenient by design.</b> Normal temp paths and not-yet-created files
/// inside a valid base directory MUST pass — this is a narrow guard that only
/// rejects two specific conditions (reparse point, base-escape), never normal
/// storage paths. When the target file does not yet exist we validate the
/// nearest existing parent directory instead of the missing leaf.</para>
///
/// <para>Pure / static. <see cref="IsSafe"/> never throws (resolution failures
/// are treated as the path being unverifiable, but the dominant check is the
/// normalized base-containment compare which does not throw for normal input).
/// <see cref="EnsureSafe"/> throws <see cref="SecurityException"/> on rejection
/// and best-effort records the rejection to the shared diag log.</para>
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is a normal, non-symlink
    /// path resolving inside <paramref name="expectedBaseDir"/>; <c>false</c>
    /// when the path (or an existing parent) carries
    /// <see cref="FileAttributes.ReparsePoint"/>, or when the resolved full path
    /// does not sit under <see cref="Path.GetFullPath(string)"/> of the base.
    ///
    /// <para>For a not-yet-created file inside a valid base directory we validate
    /// the existing parent chain (not the missing leaf) so first-write paths are
    /// accepted. If either argument is null/empty the path is treated as unsafe.</para>
    /// </summary>
    public static bool IsSafe(string path, string expectedBaseDir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(expectedBaseDir))
            return false;

        try
        {
            // Normalized base with a trailing separator so a StartsWith compare
            // cannot be fooled by a sibling whose name shares the base prefix
            // (e.g. "C:\\SenseEase" must not match "C:\\SenseEaseEvil").
            string baseFull = Path.GetFullPath(expectedBaseDir);
            string baseWithSep = baseFull;
            if (!baseWithSep.EndsWith(Path.DirectorySeparatorChar) &&
                !baseWithSep.EndsWith(Path.AltDirectorySeparatorChar))
            {
                baseWithSep += Path.DirectorySeparatorChar;
            }

            // Resolve the path. When the target exists and is (or links through)
            // a reparse point, ResolveLinkTarget(returnFinalTarget:true) yields a
            // non-null final target; we treat any reparse traversal as unsafe.
            var info = new FileInfo(path);
            if (info.Exists || Directory.Exists(path))
            {
                FileSystemInfo? finalTarget = ResolveLinkTargetSafe(info);
                if (finalTarget is not null)
                {
                    // The path itself is a link → reject (reparse point).
                    return false;
                }
                if (HasReparsePoint(info))
                    return false;
            }

            // Walk the existing parent chain; if any existing ancestor is a
            // reparse point the path could be redirected out of the base.
            if (ExistingAncestorIsReparsePoint(path))
                return false;

            // Final containment check against the normalized, separator-terminated
            // base. Path.GetFullPath collapses any ".." traversal first.
            string full = Path.GetFullPath(path);

            // Allow the base directory itself (full == baseFull) as well as
            // anything strictly under it.
            if (string.Equals(full, baseFull, StringComparison.OrdinalIgnoreCase))
                return true;

            return full.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Unresolvable / malformed path — cannot prove it is safe.
            return false;
        }
    }

    /// <summary>
    /// Throws <see cref="SecurityException"/> when <paramref name="path"/> fails
    /// <see cref="IsSafe"/>; otherwise returns normally. On rejection it
    /// best-effort appends a redacted note to the shared diagnostic log.
    /// </summary>
    public static void EnsureSafe(string path, string expectedBaseDir)
    {
        if (IsSafe(path, expectedBaseDir))
            return;

        try
        {
            WgcDiagLog.Append(
                "Security: rejected reparse point / out-of-base path: <redacted>");
        }
        catch
        {
            // Logging is best-effort; never mask the security rejection.
        }

        throw new SecurityException(
            "Rejected reparse point or out-of-base path.");
    }

    /// <summary>True when <paramref name="info"/> exists and its own attributes
    /// carry the reparse-point flag.</summary>
    private static bool HasReparsePoint(FileSystemInfo info)
    {
        try
        {
            return info.Exists &&
                   (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            // Attribute read failed — be conservative.
            return true;
        }
    }

    /// <summary>Best-effort <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/>:
    /// returns the final target when the entry is a link, else null. Swallows the
    /// IOException thrown when the entry is not a link.</summary>
    private static FileSystemInfo? ResolveLinkTargetSafe(FileSystemInfo info)
    {
        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks up from <paramref name="path"/> to the first existing
    /// ancestor directory and returns true if any existing ancestor on the chain
    /// is a reparse point. A not-yet-created leaf whose existing parent is a
    /// normal directory is therefore accepted.</summary>
    private static bool ExistingAncestorIsReparsePoint(string path)
    {
        try
        {
            string? current = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(current))
            {
                var dir = new DirectoryInfo(current);
                if (dir.Exists)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        return true;
                    // Also catch the case where this existing ancestor is itself
                    // a link (junction created without the attribute surfacing).
                    if (ResolveLinkTargetSafe(dir) is not null)
                        return true;
                }
                DirectoryInfo? parent = dir.Parent;
                if (parent is null)
                    break;
                current = parent.FullName;
            }
        }
        catch
        {
            // On any failure, be conservative and flag as unsafe.
            return true;
        }
        return false;
    }
}
