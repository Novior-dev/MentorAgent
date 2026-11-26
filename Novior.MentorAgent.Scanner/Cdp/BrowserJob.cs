using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Novior.MentorAgent.Scanner.Cdp;

/// <summary>
/// Story 1636, slice B — on Windows, the scan's browser runs in a job object of its own that closes it when the job is
/// closed (<c>page.scan-browser</c>). The application holds the only handle to the job: when it ends in any way, also
/// killed without stopping, Windows closes the handle, and with it the browser and its DevTools port. Elsewhere, or when
/// Windows refuses the job, there is none, and the next scan closes a browser left by an application that died
/// (<see cref="ScanLeftovers"/>).
/// </summary>
internal sealed class BrowserJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _handle;

    private BrowserJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>
    /// Puts <paramref name="process"/> in a new job that closes it with the job; <see langword="null"/> when this is not
    /// Windows or Windows refuses it.
    /// </summary>
    internal static BrowserJob? TryContain(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Contain(process);
    }

    [SupportedOSPlatform("windows")]
    private static BrowserJob? Contain(Process process)
    {
        // Not inheritable: the browser must not hold the job open itself.
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var limits = new JobObjectExtendedLimit();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimit>())
            || !AssignProcessToJobObject(handle, process.Handle))
        {
            handle.Dispose();
            return null;
        }
        return new BrowserJob(handle);
    }

    /// <summary>Closes the job, and with it whatever of the browser still runs.</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>Whether the job is closed, and with it whatever of the browser was left in it.</summary>
    internal bool IsClosed => _handle.IsClosed;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
