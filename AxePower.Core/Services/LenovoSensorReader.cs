using System;
using System.Linq;
using Microsoft.Management.Infrastructure;

namespace AxePower.Core.Services;

/// <summary>
/// Reads CPU and GPU temperature from Lenovo's <c>LENOVO_GAMEZONE_DATA</c> ACPI-WMI interface,
/// the same one Lenovo Vantage and Legion Toolkit use.
///
/// This matters more than it looks. The usual way to read a CPU die temperature is to load a
/// kernel driver and read the thermal MSRs — what LibreHardwareMonitor, MSI Afterburner and
/// HWiNFO all do. On current Windows 11 that driver (WinRing0) is on Microsoft's vulnerable
/// driver blocklist, so with Memory Integrity or the blocklist enabled — the default on new
/// machines — it silently fails to load and every MSR-based reading comes back empty.
///
/// The firmware route has no such problem: it is a WMI method call into ACPI, needs no driver,
/// and is unaffected by HVCI. It does need administrator rights, which is the concrete reason
/// <see cref="Models.AppSettings.RunAsAdministrator"/> defaults to on.
///
/// The calls go through the MI (CIM) stack rather than <c>System.Management</c> on purpose.
/// Every legacy <c>ManagementObject.InvokeMethod</c> form — with and without resolved parameter
/// objects, explicit paths, or a privileged scope — fails against this provider with a bare
/// "Not found", while the same call over MI, which is what PowerShell's <c>Invoke-CimMethod</c>
/// uses, succeeds.
///
/// Values are degrees Celsius, with anything below 1 meaning "not reported" (Legion Toolkit
/// treats the interface the same way).
/// </summary>
public class LenovoSensorReader : IDisposable
{
    private const string Namespace = @"root\WMI";
    private const string Query = "SELECT * FROM LENOVO_GAMEZONE_DATA";

    /// <summary>Plausible die temperature range; outside it the firmware is reporting nonsense.</summary>
    private const double MinValidC = 1;
    private const double MaxValidC = 125;

    /// <summary>
    /// After this many consecutive failures the interface is given up on for the session. The
    /// usual cause is a missing elevation, which cannot change without a restart, so retrying
    /// every tick would only spend WMI calls and fill the log.
    /// </summary>
    private const int MaxConsecutiveFailures = 3;

    private readonly LogService? _logService;
    private readonly object _lock = new();

    private CimSession? _session;
    private CimInstance? _instance;
    private bool _isDisposed;
    private bool _givenUp;
    private int _consecutiveFailures;

    public LenovoSensorReader(LogService? logService = null)
    {
        _logService = logService;
    }

    /// <summary>True when this machine exposes the interface and it answered a method call.</summary>
    public bool IsAvailable { get; private set; }

    public double GetCpuTempC() => InvokeTemperature("GetCPUTemp");

    public double GetGpuTempC() => InvokeTemperature("GetGPUTemp");

    private double InvokeTemperature(string method)
    {
        lock (_lock)
        {
            if (_isDisposed || _givenUp) return double.NaN;

            try
            {
                if (!EnsureSession()) return double.NaN;

                using var result = _session!.InvokeMethod(Namespace, _instance, method, null);

                // The interface reports success separately from the payload
                if (result.OutParameters["ReturnValue"]?.Value is bool ok && !ok) return double.NaN;

                var data = result.OutParameters["Data"]?.Value;
                if (data == null) return double.NaN;

                double value = Convert.ToDouble(data);

                // A few firmware revisions report Kelvin, or deci-Kelvin, instead of Celsius
                if (value > 2000) value = (value / 10.0) - 273.15;
                else if (value > 200) value -= 273.15;

                IsAvailable = true;
                _consecutiveFailures = 0;
                return value is >= MinValidC and <= MaxValidC ? value : double.NaN;
            }
            catch (Exception ex)
            {
                // Most often "Access denied" when not elevated.
                IsAvailable = false;
                ReleaseSession();

                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _givenUp = true;
                    _logService?.LogWarning(
                        $"Lenovo sensor interface unavailable ({ex.Message.Trim()}); not retrying this session.");
                }

                return double.NaN;
            }
        }
    }

    /// <summary>
    /// Opens the CIM session and resolves the single GameZone instance, both of which are held
    /// for the life of the reader — unlike the legacy stack, an MI instance stays valid.
    /// </summary>
    private bool EnsureSession()
    {
        if (_session != null && _instance != null) return true;

        ReleaseSession();

        _session = CimSession.Create(null);
        _instance = _session.QueryInstances(Namespace, "WQL", Query).FirstOrDefault();

        if (_instance != null) return true;

        // Not a Lenovo machine, or the ACPI interface is absent — an ordinary outcome.
        ReleaseSession();
        _givenUp = true;
        _logService?.LogInfo("Lenovo sensor interface is not present on this machine.");
        return false;
    }

    private void ReleaseSession()
    {
        try { _instance?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        _instance = null;
        _session = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            ReleaseSession();
        }
    }
}
