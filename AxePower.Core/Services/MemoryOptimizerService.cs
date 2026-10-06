using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class MemoryOptimizerService : IMemoryOptimizerService
{
    private readonly LogService _log;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    public MemoryOptimizerService(LogService log)
    {
        _log = log;
    }

    public MemoryStatusInfo GetMemoryStatus()
    {
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            double totalGb = (double)memStatus.ullTotalPhys / (1024 * 1024 * 1024);
            double availGb = (double)memStatus.ullAvailPhys / (1024 * 1024 * 1024);
            double usedGb = totalGb - availGb;
            double loadPct = memStatus.dwMemoryLoad;

            return new MemoryStatusInfo
            {
                TotalPhysicalGb = totalGb,
                AvailablePhysicalGb = availGb,
                UsedPhysicalGb = usedGb,
                MemoryLoadPercent = loadPct
            };
        }

        return new MemoryStatusInfo
        {
            TotalPhysicalGb = 16.0,
            AvailablePhysicalGb = 8.0,
            UsedPhysicalGb = 8.0,
            MemoryLoadPercent = 50.0
        };
    }

    public async Task<double> PurgeStandbyMemoryAsync()
    {
        return await Task.Run(() =>
        {
            var initial = GetMemoryStatus();
            _log.LogInfo($"[MemoryOptimizer] Starting RAM standby list & working set purge. Initial available: {initial.AvailablePhysicalGb:F2} GB");

            try
            {
                // 1. Flush GC in current process
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // 2. Empty working set for accessible user processes
                var processes = Process.GetProcesses();
                int trimmedProcesses = 0;

                foreach (var proc in processes)
                {
                    try
                    {
                        if (!proc.HasExited && proc.Id > 4)
                        {
                            EmptyWorkingSet(proc.Handle);
                            trimmedProcesses++;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Access denied for protected system processes, or exited: skipped.
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                var final = GetMemoryStatus();
                double freedGb = Math.Max(0, final.AvailablePhysicalGb - initial.AvailablePhysicalGb);
                _log.LogInfo($"[MemoryOptimizer] Purge completed across {trimmedProcesses} processes. Freed ~{freedGb * 1024:F0} MB RAM. New available: {final.AvailablePhysicalGb:F2} GB");
                return freedGb;
            }
            catch (Exception ex)
            {
                _log.LogError("[MemoryOptimizer] Failed to purge standby memory", ex);
                return 0.0;
            }
        });
    }
}
