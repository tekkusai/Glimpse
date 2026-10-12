using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpacePreview.Services;

/// <summary>
/// A Windows job whose processes end when Glimpse does. A helper that is
/// put in one cannot outlive the app, however the app ends: quit, crash or
/// replaced by an update. Windows ends everything in the job when its last
/// handle closes, and that handle closes with the process. Whatever a
/// helper starts joins the job too.
/// </summary>
internal sealed class ChildJob : IDisposable
{
    private readonly JobHandle _job;

    /// <param name="priority">Every process in the job runs at this priority and cannot raise itself.</param>
    public ChildJob(ProcessPriorityClass? priority = null)
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid)
        {
            throw new Win32Exception();
        }

        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = KillOnJobClose;
        if (priority is { } value)
        {
            limits.Basic.LimitFlags |= PriorityClassLimit;
            limits.Basic.PriorityClass = (uint)value;
        }

        if (!SetInformationJobObject(_job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            _job.Dispose();
            throw error;
        }
    }

    /// <summary>
    /// Puts a started process in the job. False when Windows refuses or
    /// the process has already gone: it then runs, or ran, as it did
    /// before jobs. A process that starts a child in the instant before
    /// this call leaves that child outside.
    /// </summary>
    public bool Add(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>How many processes in the job are still running. Zero once the handle is closed.</summary>
    public int ActiveProcesses
    {
        get
        {
            if (_job.IsClosed)
            {
                return 0;
            }

            return QueryInformationJobObject(_job, BasicAccountingInformation, out var accounting, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)
                ? (int)accounting.ActiveProcesses
                : 0;
        }
    }

    /// <summary>Ends every process in the job now. The job stays usable.</summary>
    public void Terminate()
    {
        if (!_job.IsClosed)
        {
            TerminateJobObject(_job, 1);
        }
    }

    /// <summary>Closes the handle, which ends every process still in the job.</summary>
    public void Dispose() => _job.Dispose();

    private const uint KillOnJobClose = 0x2000;
    private const uint PriorityClassLimit = 0x20;
    private const int BasicAccountingInformation = 1;
    private const int ExtendedLimitInformation = 9;

    private sealed class JobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(JobHandle job, int infoClass, out BasicAccounting info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
