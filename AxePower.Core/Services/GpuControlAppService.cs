using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace AxePower.Core.Services;

public enum DetectedGpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Unknown
}

public class GpuControlAppService
{
    private readonly LogService? _logService;
    private DetectedGpuVendor _vendor = DetectedGpuVendor.Unknown;
    private bool _detected = false;

    public GpuControlAppService(LogService? logService = null)
    {
        _logService = logService;
    }

    public DetectedGpuVendor DetectVendor()
    {
        if (_detected) return _vendor;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                {
                    _vendor = DetectedGpuVendor.Nvidia;
                    _detected = true;
                    return _vendor;
                }
                else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    _vendor = DetectedGpuVendor.Amd;
                    _detected = true;
                    return _vendor;
                }
                else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                {
                    _vendor = DetectedGpuVendor.Intel;
                }
            }
        }
        catch (Exception ex)
        {
            _logService?.LogWarning($"Failed to query GPU vendor: {ex.Message}");
        }

        _detected = true;
        return _vendor;
    }

    public string GetBrandColorHex()
    {
        return DetectVendor() switch
        {
            DetectedGpuVendor.Nvidia => "#76B900", // NVIDIA signature lime green
            DetectedGpuVendor.Amd => "#ED1C24",    // AMD Radeon red
            DetectedGpuVendor.Intel => "#0071C5",  // Intel blue
            _ => "#76B900"
        };
    }

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public bool OpenGraphicsSettings()
    {
        var vendor = DetectVendor();

        if (vendor == DetectedGpuVendor.Nvidia || vendor == DetectedGpuVendor.Unknown)
        {
            // 1. Try NVIDIA App
            // Built from the shell's folder constants rather than spelled out: Windows is not
            // always installed on C:, and on those machines a literal path finds nothing.
            string[] nvidiaAppPaths = {
                Path.Combine(ProgramFiles, @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
                Path.Combine(ProgramFilesX86, @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
                Path.Combine(ProgramData, @"Microsoft\Windows\Start Menu\Programs\NVIDIA Corporation\NVIDIA App.lnk")
            };

            foreach (var path in nvidiaAppPaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        });
                        return true;
                    }
                    catch { }
                }
            }

            // 2. Try GeForce Experience
            string gfePath = Path.Combine(ProgramFiles, @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe");
            if (File.Exists(gfePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = gfePath, UseShellExecute = true });
                    return true;
                }
                catch { }
            }

            // 3. Try NVIDIA Control Panel
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "nvcplui.exe", UseShellExecute = true });
                return true;
            }
            catch { }
        }

        if (vendor == DetectedGpuVendor.Amd || vendor == DetectedGpuVendor.Unknown)
        {
            string[] amdPaths = {
                Path.Combine(ProgramFiles, @"AMD\CNext\CNext\RadeonSoftware.exe"),
                Path.Combine(ProgramFiles, @"AMD\CNext\CNext\amdair.exe")
            };

            foreach (var path in amdPaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = path,
                            Arguments = "-page games",
                            UseShellExecute = true
                        });
                        return true;
                    }
                    catch { }
                }
            }
        }

        // Fallback: Windows Graphics Settings
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:display-advancedgraphics",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
