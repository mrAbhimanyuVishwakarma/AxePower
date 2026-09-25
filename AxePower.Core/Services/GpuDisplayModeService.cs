using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Management.Infrastructure;

namespace AxePower.Core.Services;

/// <summary>The same choice as NVIDIA Control Panel's "Manage Display Mode".</summary>
public enum GpuDisplayMode
{
    /// <summary>Hybrid: the integrated GPU drives the screen and Windows picks a GPU per app.</summary>
    Optimus,

    /// <summary>The MUX wires the screen straight to the dedicated GPU.</summary>
    DedicatedOnly
}

public interface IGpuDisplayModeController
{
    /// <summary>False on non-Lenovo machines, on models without a MUX, and when not elevated.</summary>
    Task<bool> IsSupportedAsync();

    /// <summary>The mode set in firmware, which may still be waiting for a restart.</summary>
    Task<GpuDisplayMode?> GetModeAsync();

    Task<bool> SetModeAsync(GpuDisplayMode mode);

    /// <summary>True once the firmware mode differs from the one Windows booted with.</summary>
    bool RestartRequired { get; }
}

/// <summary>
/// Lenovo's MUX switch ("GPU Working Mode" in Vantage), reached through the GameZone ACPI-WMI
/// methods IsSupportGSync / GetGSyncStatus / SetGSyncStatus: status 1 is dGPU-only, 0 is
/// Optimus. A change takes effect after a restart. Uses the MI (CIM) stack for the reason
/// given on <see cref="LenovoSensorReader"/>, and needs administrator rights.
/// </summary>
public sealed class LenovoGpuDisplayModeService : IGpuDisplayModeController
{
    private const string Namespace = @"root\WMI";
    private const string Query = "SELECT * FROM LENOVO_GAMEZONE_DATA";

    private readonly LogService _log;
    private readonly object _lock = new();
    private GpuDisplayMode? _bootMode;
    private GpuDisplayMode? _lastMode;

    public LenovoGpuDisplayModeService(LogService log)
    {
        _log = log;
    }

    public bool RestartRequired => _bootMode != null && _lastMode != null && _bootMode != _lastMode;

    public Task<bool> IsSupportedAsync() => Task.Run(() => Invoke("IsSupportGSync") is > 0);

    public Task<GpuDisplayMode?> GetModeAsync() => Task.Run(() =>
    {
        var status = Invoke("GetGSyncStatus");
        if (status == null) return (GpuDisplayMode?)null;

        var mode = status == 1 ? GpuDisplayMode.DedicatedOnly : GpuDisplayMode.Optimus;
        lock (_lock)
        {
            _bootMode ??= mode;
            _lastMode = mode;
        }
        return mode;
    });

    public Task<bool> SetModeAsync(GpuDisplayMode mode) => Task.Run(async () =>
    {
        // Read first, so the mode Windows booted with is known before it changes
        await GetModeAsync();

        int data = mode == GpuDisplayMode.DedicatedOnly ? 1 : 0;
        if (Invoke("SetGSyncStatus", data) == null)
            return false;

        var now = await GetModeAsync();
        bool ok = now == mode;
        if (ok) _log.LogInfo($"GPU display mode set to {mode}; takes effect after a restart.");
        else _log.LogWarning($"GPU display mode change to {mode} did not stick (reads {now}).");
        return ok;
    });

    /// <summary>Returns the method's Data (0 when it has none), or null when the call failed.</summary>
    private int? Invoke(string method, int? data = null)
    {
        try
        {
            using var session = CimSession.Create(null);
            using var instance = session.QueryInstances(Namespace, "WQL", Query).FirstOrDefault();
            if (instance == null) return null;

            using var parameters = new CimMethodParametersCollection();
            if (data != null)
                parameters.Add(CimMethodParameter.Create("Data", (uint)data.Value, CimType.UInt32, CimFlags.In));

            using var result = session.InvokeMethod(Namespace, instance, method, parameters);
            if (result.OutParameters["ReturnValue"]?.Value is bool ok && !ok) return null;

            var value = result.OutParameters["Data"]?.Value;
            return value == null ? 0 : Convert.ToInt32(value);
        }
        catch (Exception ex)
        {
            // Usually "Access denied" (not elevated) or "Not found" (no such method on this model)
            _log.LogInfo($"Lenovo GPU display mode: {method} unavailable ({ex.Message.Trim()}).");
            return null;
        }
    }
}
