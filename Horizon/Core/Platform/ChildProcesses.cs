using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Horizon.Core.Platform;

/// <summary>
/// Programs started to serve this one (a language server, a debugger) tied to its life, so they go when it goes however
/// it goes. On Windows they're put in a job that kills everything in it once the last handle to it closes, which the
/// system does for a process that was killed too. serve-d and its dcd-server were left running after every Hex that was
/// ended from outside. Elsewhere it does nothing, a child there goes with its terminal or is the system's to reap.
/// </summary>
public static partial class ChildProcesses
{
    private static readonly Lock gate = new();
    private static IntPtr job;

    /// <summary>Ties a process that was just started to this one's life, nothing when it can't be.</summary>
    public static void Adopt(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            lock (gate)
            {
                if (job == IntPtr.Zero)
                    job = MakeJob();
                if (job != IntPtr.Zero)
                    AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception)
        {
            // Gone already, or not allowed in a job, it's left as it was
        }
    }

    private static IntPtr MakeJob()
    {
        IntPtr made = CreateJobObjectW(IntPtr.Zero, null);
        if (made == IntPtr.Zero)
            return IntPtr.Zero;

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, info, false);
            if (!SetInformationJobObject(made, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS, info, (uint)size))
                return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }

        return made;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS = 9;

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

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
