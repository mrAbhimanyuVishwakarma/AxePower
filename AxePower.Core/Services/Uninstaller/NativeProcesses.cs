using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>One row of a Toolhelp process snapshot.</summary>
public readonly record struct ProcessSnapshotEntry(int Pid, int ParentPid, string ExeName);

/// <summary>
/// Process queries that work across integrity levels and bitness. Toolhelp snapshots list
/// elevated processes too, and PROCESS_QUERY_LIMITED_INFORMATION opens them for their path and
/// creation time — where <c>Process.MainModule</c> throws.
/// </summary>
internal static class NativeProcesses
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle hProcess,
        out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    /// <summary>Every running process with its parent PID. Empty if the snapshot fails.</summary>
    public static List<ProcessSnapshotEntry> Snapshot()
    {
        var list = new List<ProcessSnapshotEntry>(256);
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue) return list;

        try
        {
            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return list;
            do
            {
                list.Add(new ProcessSnapshotEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID,
                    entry.szExeFile ?? string.Empty));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }

    /// <summary>A handle that can be waited on and queried; null when access is denied or the process is gone.</summary>
    public static SafeProcessHandle? OpenForQuery(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    /// <summary>Creation time as a UTC FILETIME, or null when it can't be read.</summary>
    public static long? GetCreationTime(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null;

    public static long? GetCreationTime(int pid)
    {
        using var handle = OpenForQuery(pid);
        return handle is null ? null : GetCreationTime(handle);
    }

    /// <summary>Full image path, or null when it can't be read.</summary>
    public static string? GetImagePath(SafeProcessHandle handle)
    {
        var size = 1024u;
        var buffer = new StringBuilder((int)size);
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    public static string? GetImagePath(int pid)
    {
        using var handle = OpenForQuery(pid);
        return handle is null ? null : GetImagePath(handle);
    }

    public static bool HasExited(SafeProcessHandle handle) =>
        WaitForSingleObject(handle, 0) == WaitObject0;

    public static int? GetExitCode(SafeProcessHandle handle) =>
        GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : null;

    /// <summary>Terminates a process. False when access is denied (e.g. it runs elevated and we don't).</summary>
    public static bool Terminate(int pid)
    {
        using var handle = OpenProcess(ProcessTerminate, false, pid);
        if (handle.IsInvalid) return false;
        return TerminateProcess(handle, 1);
    }
}
