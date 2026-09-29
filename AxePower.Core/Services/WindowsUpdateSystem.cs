using System;
using System.IO;
using Microsoft.Win32;
using System.ServiceProcess;

namespace AxePower.Core.Services;

internal readonly record struct WindowsServiceState(bool Exists, bool IsRunning, string Detail);
internal readonly record struct WindowsUpdateOperationResult(bool Success, string Detail);

internal interface IWindowsUpdateSystem
{
    int? ReadDword(string keyPath, string valueName);
    void WriteDword(string keyPath, string valueName, int value);
    void DeleteValue(string keyPath, string valueName);
    WindowsServiceState GetServiceState(string serviceName);
    WindowsUpdateOperationResult StopService(string serviceName, TimeSpan timeout);
    WindowsUpdateOperationResult StartService(string serviceName, TimeSpan timeout);
    WindowsUpdateOperationResult ClearUpdateDownloadCache();
}

internal sealed class WindowsUpdateSystem : IWindowsUpdateSystem
{
    public int? ReadDword(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    public void WriteDword(string keyPath, string valueName, int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Windows did not open HKLM\\{keyPath} for writing.");
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public WindowsServiceState GetServiceState(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            service.Refresh();
            bool running = service.Status != ServiceControllerStatus.Stopped;
            return new WindowsServiceState(true, running, service.Status.ToString());
        }
        catch (InvalidOperationException ex)
        {
            return new WindowsServiceState(false, false, ex.Message);
        }
    }

    public WindowsUpdateOperationResult StopService(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped)
                return new WindowsUpdateOperationResult(true, "already stopped");
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return new WindowsUpdateOperationResult(true, "stopped");
        }
        catch (Exception ex)
        {
            return new WindowsUpdateOperationResult(false, ex.Message);
        }
    }

    public WindowsUpdateOperationResult StartService(string serviceName, TimeSpan timeout)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Running)
                return new WindowsUpdateOperationResult(true, "already running");
            if (service.Status != ServiceControllerStatus.StartPending) service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return new WindowsUpdateOperationResult(true, "running");
        }
        catch (Exception ex)
        {
            return new WindowsUpdateOperationResult(false, ex.Message);
        }
    }

    public WindowsUpdateOperationResult ClearUpdateDownloadCache()
    {
        try
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windows))
                return new WindowsUpdateOperationResult(false, "Windows directory could not be located.");

            string cache = Path.Combine(windows, "SoftwareDistribution", "Download");
            if (!Directory.Exists(cache))
            {
                Directory.CreateDirectory(cache);
                return new WindowsUpdateOperationResult(true, $"Cache was already empty: {cache}");
            }

            // Services are stopped before this runs. Replacing the Download directory is more
            // reliable than swallowing hundreds of individual deletion failures.
            string oldCache = cache + ".AxePower-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            Directory.Move(cache, oldCache);
            Directory.CreateDirectory(cache);
            Directory.Delete(oldCache, recursive: true);
            return new WindowsUpdateOperationResult(true, $"Cleared update downloads from: {cache}");
        }
        catch (Exception ex)
        {
            return new WindowsUpdateOperationResult(false, $"Could not clear the update download cache: {ex.Message}");
        }
    }
}
