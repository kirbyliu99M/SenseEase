using System.Runtime.InteropServices;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Windows Job Object that auto-kills any process assigned to it when this
/// process exits — even if we die via Stop-Process / Task Manager / crash, so
/// the OS reaps our llama-server child instead of leaving it as an orphan
/// holding 2 GB of VRAM. Without this safety net, repeated dev runs accumulate
/// llama-server zombies that eventually exhaust GPU memory.
///
/// Pattern: create a JobObject with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, then
/// AssignProcessToJobObject(child). When this process's handle to the job
/// closes (i.e. the process exits for any reason), Windows kills everything in
/// the job.
/// </summary>
internal static class ChildProcessReaper
{
    private static readonly IntPtr _job;

    /// <summary>SIDE-05: populated when the static constructor or
    /// <see cref="Adopt"/> encounters a Win32 / exception failure. Null when
    /// everything succeeded. Callers (currently SidecarHeadPoseReader and
    /// MistralChatbotService) read this once after their first Adopt call and
    /// route it to their own diag sinks so the failure is surfaced without
    /// making the reaper fatal.</summary>
    internal static string? LastError { get; private set; }

    static ChildProcessReaper()
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"[ChildProcessReaper] CreateJobObject failed (Win32 error {err}). Job-object reaping disabled.";
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                }
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr p = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, p, false);
                bool ok = SetInformationJobObject(_job, JobObjectExtendedLimitInformation, p, (uint)len);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    LastError = $"[ChildProcessReaper] SetInformationJobObject failed (Win32 error {err}). KILL_ON_JOB_CLOSE limit may not be set.";
                }
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch (Exception ex)
        {
            _job = IntPtr.Zero;
            LastError = $"[ChildProcessReaper] Static constructor threw {ex.GetType().Name}: {ex.Message}. Job-object reaping disabled.";
        }
    }

    /// <summary>Attach the given process to the reaper job. Best-effort: on
    /// failure (older Windows, security context, etc.) the child just isn't
    /// reaped and we fall back to normal Dispose-time Kill.
    ///
    /// <para>SIDE-05: on Win32 failure, appends a diagnostic to
    /// <see cref="LastError"/> (overwriting any prior ctor message). The caller
    /// should check <see cref="LastError"/> once after this call and log it via
    /// its own diag sink — the reaper itself has no log sink.</para></summary>
    public static void Adopt(System.Diagnostics.Process child)
    {
        if (_job == IntPtr.Zero || child is null) return;
        try
        {
            bool ok = AssignProcessToJobObject(_job, child.Handle);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"[ChildProcessReaper] AssignProcessToJobObject failed for pid={child.Id} (Win32 error {err}). Child will not be auto-reaped.";
            }
        }
        catch (Exception ex)
        {
            LastError = $"[ChildProcessReaper] AssignProcessToJobObject threw {ex.GetType().Name}: {ex.Message}. Child will not be auto-reaped.";
        }
    }

    // ── Win32 P/Invoke ──────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int  JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
