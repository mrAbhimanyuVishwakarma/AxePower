using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services.Processes;

/// <summary>
/// One run's Windows job object. Everything the started process launches joins the job too, so
/// cancelling a run ends that run's whole tree and nothing else. <c>KILL_ON_JOB_CLOSE</c> also
/// covers AxePower dying mid-run. A run that ends normally calls <see cref="Release"/> first, so
/// a program the installer opened on purpose (an app that starts after setup) keeps running.
/// </summary>
internal sealed class ProcessTreeJob : IDisposable
{
    private IntPtr _handle;

    public ProcessTreeJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle != IntPtr.Zero) SetLimitFlags(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE);
    }

    /// <summary>False when no job could be made or the process already exited; the caller then falls back to a tree kill.</summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(_handle, process.Handle); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Ends every process in the job.</summary>
    public bool Terminate() => _handle != IntPtr.Zero && TerminateJobObject(_handle, 1);

    /// <summary>Lets whatever is still in the job outlive it.</summary>
    public void Release()
    {
        if (_handle != IntPtr.Zero) SetLimitFlags(0);
    }

    private void SetLimitFlags(uint flags)
    {
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = flags }
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    // -- Win32 ---------------------------------------------------------------

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
