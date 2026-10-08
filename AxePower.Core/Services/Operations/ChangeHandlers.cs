using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;

namespace AxePower.Core.Services.Operations;

/// <summary>Reads, writes and describes one kind of Windows state. Exceptions mean the step failed.</summary>
public interface IChangeHandler
{
    IReadOnlyCollection<string> Kinds { get; }

    ChangeState Read(string kind, string target);

    void Apply(string kind, string target, ChangeState state);

    string Describe(string kind, string target, ChangeState state);

    bool RequiresAdministrator(string kind, string target);

    (Reversibility Level, string? Note) ReversibilityOf(string kind, string target);

    /// <summary>Whether two states mean the same thing (by default, the same content).</summary>
    bool Same(string kind, ChangeState a, ChangeState b) => a.SameAs(b);
}

// -- Registry ------------------------------------------------------------------

/// <summary>
/// Registry values and keys. A value's snapshot keeps its exact type and data (or that it was
/// absent), so rollback restores it bit for bit. A created key remembers which ancestor was missing,
/// so rollback removes exactly what was created and nothing above it.
/// Targets: "HKCU\Path\To\Key|ValueName" for values ("" is the default value), "HKCU\Path\To\Key" for keys.
/// </summary>
public sealed class RegistryChangeHandler : IChangeHandler
{
    public const string ValueKind = "registry.value";
    public const string KeyKind = "registry.key";

    private readonly Func<RegistryHive, RegistryKey> _root;

    /// <param name="root">The base key for a hive. Tests map hives to a scratch key under HKCU.</param>
    public RegistryChangeHandler(Func<RegistryHive, RegistryKey>? root = null)
    {
        _root = root ?? DefaultRoot;
    }

    public IReadOnlyCollection<string> Kinds { get; } = new[] { ValueKind, KeyKind };

    private static RegistryKey DefaultRoot(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => Registry.LocalMachine,
        RegistryHive.CurrentUser => Registry.CurrentUser,
        _ => throw new NotSupportedException($"AxePower does not change {hive}."),
    };

    // -- Requests features build --------------------------------------------------

    public static ChangeRequest SetDword(string key, string name, int value) =>
        new(ValueKind, $"{key}|{name}", ChangeState.Of(("exists", "true"), ("kind", nameof(RegistryValueKind.DWord)), ("data", value.ToString(CultureInfo.InvariantCulture))));

    public static ChangeRequest SetString(string key, string name, string value) =>
        new(ValueKind, $"{key}|{name}", ChangeState.Of(("exists", "true"), ("kind", nameof(RegistryValueKind.String)), ("data", value)));

    public static ChangeRequest DeleteValue(string key, string name) =>
        new(ValueKind, $"{key}|{name}", ChangeState.Of(("exists", "false")));

    public static ChangeRequest EnsureKey(string key) => new(KeyKind, key, ChangeState.Of(("exists", "true")));

    /// <summary>Removes <paramref name="key"/> and everything under it.</summary>
    public static ChangeRequest RemoveKey(string key) =>
        new(KeyKind, key, ChangeState.Of(("exists", "false"), ("missingFrom", key[(key.IndexOf('\\') + 1)..])));

    // -- IChangeHandler -----------------------------------------------------------

    public ChangeState Read(string kind, string target)
    {
        if (kind == KeyKind)
        {
            var (hive, path) = SplitKey(target);
            using (var key = _root(hive).OpenSubKey(path)) if (key is not null) return ChangeState.Of(("exists", "true"));
            return ChangeState.Of(("exists", "false"), ("missingFrom", TopmostMissing(hive, path)));
        }

        var (valueHive, keyPath, name) = SplitValue(target);
        using var parent = _root(valueHive).OpenSubKey(keyPath);
        if (parent is null) return ChangeState.Of(("exists", "false"), ("missingFrom", TopmostMissing(valueHive, keyPath)));
        if (!parent.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return ChangeState.Of(("exists", "false"));

        var valueKind = parent.GetValueKind(name);
        var raw = parent.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return ChangeState.Of(("exists", "true"), ("kind", valueKind.ToString()), ("data", Encode(valueKind, raw)));
    }

    public void Apply(string kind, string target, ChangeState state)
    {
        bool exists = state["exists"] == "true";
        if (kind == KeyKind)
        {
            var (hive, path) = SplitKey(target);
            if (exists)
            {
                using var created = _root(hive).CreateSubKey(path, writable: true)
                                    ?? throw new UnauthorizedAccessException($"Could not create {target}.");
            }
            else
            {
                // Remove only what was created: the topmost key that was missing before.
                var missingFrom = state["missingFrom"] is { Length: > 0 } m ? m : path;
                _root(hive).DeleteSubKeyTree(missingFrom, throwOnMissingSubKey: false);
            }
            return;
        }

        var (valueHive, keyPath, name) = SplitValue(target);
        if (!exists)
        {
            using (var parent = _root(valueHive).OpenSubKey(keyPath, writable: true))
                parent?.DeleteValue(name, throwOnMissingValue: false);
            // The key did not exist before either: remove the keys setting the value created, but
            // only while they are still empty, so nothing written there since is lost.
            if (state["missingFrom"] is { Length: > 0 } missingFrom) PruneEmpty(valueHive, keyPath, missingFrom);
            return;
        }

        var valueKind = Enum.Parse<RegistryValueKind>(state["kind"] ?? nameof(RegistryValueKind.String));
        using var key = _root(valueHive).CreateSubKey(keyPath, writable: true)
                        ?? throw new UnauthorizedAccessException($"Could not open {keyPath}.");
        key.SetValue(name, Decode(valueKind, state["data"] ?? string.Empty), valueKind);
    }

    public string Describe(string kind, string target, ChangeState state)
    {
        if (kind == KeyKind) return state["exists"] == "true" ? "present" : "absent";
        if (state["exists"] != "true") return "not set";
        return state["kind"] switch
        {
            nameof(RegistryValueKind.DWord) or nameof(RegistryValueKind.QWord) => $"{state["data"]} ({state["kind"]})",
            nameof(RegistryValueKind.Binary) => $"{Convert.FromBase64String(state["data"] ?? string.Empty).Length} bytes (Binary)",
            _ => $"\"{state["data"]}\" ({state["kind"]})",
        };
    }

    public bool RequiresAdministrator(string kind, string target) =>
        target.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) || target.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase);

    public (Reversibility Level, string? Note) ReversibilityOf(string kind, string target) => (Reversibility.FullyReversible, null);

    /// <summary>"Not set" is "not set", whichever keys above it were missing.</summary>
    public bool Same(string kind, ChangeState a, ChangeState b) =>
        a["exists"] == "false" && b["exists"] == "false" ? true : a.SameAs(b);

    /// <summary>Deletes <paramref name="keyPath"/> and its parents up to <paramref name="stopAt"/>, each only if empty.</summary>
    private void PruneEmpty(RegistryHive hive, string keyPath, string stopAt)
    {
        var path = keyPath.TrimEnd('\\');
        while (path.Length >= stopAt.Length && path.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase))
        {
            using (var key = _root(hive).OpenSubKey(path))
            {
                if (key is null) { }
                else if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
                else _root(hive).DeleteSubKey(path, throwOnMissingSubKey: false);
            }
            if (path.Length == stopAt.Length) return;
            var slash = path.LastIndexOf('\\');
            if (slash < 0) return;
            path = path[..slash];
        }
    }

    // -- Parsing and encoding -----------------------------------------------------

    private static (RegistryHive Hive, string Path) SplitKey(string target)
    {
        var slash = target.IndexOf('\\');
        if (slash <= 0) throw new ArgumentException($"Not a registry key: {target}");
        var hive = target[..slash].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            var other => throw new NotSupportedException($"AxePower does not change {other}."),
        };
        return (hive, target[(slash + 1)..]);
    }

    private static (RegistryHive Hive, string KeyPath, string Name) SplitValue(string target)
    {
        var bar = target.LastIndexOf('|');
        if (bar < 0) throw new ArgumentException($"Not a registry value: {target}");
        var (hive, path) = SplitKey(target[..bar]);
        return (hive, path, target[(bar + 1)..]);
    }

    private string TopmostMissing(RegistryHive hive, string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= parts.Length; i++)
        {
            var prefix = string.Join('\\', parts.Take(i));
            using var key = _root(hive).OpenSubKey(prefix);
            if (key is null) return prefix;
        }
        return path;
    }

    private static string Encode(RegistryValueKind kind, object? raw) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt32(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToBase64String(raw as byte[] ?? Array.Empty<byte>()),
        RegistryValueKind.MultiString => JsonSerializer.Serialize(raw as string[] ?? Array.Empty<string>()),
        _ => raw?.ToString() ?? string.Empty,
    };

    private static object Decode(RegistryValueKind kind, string data) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(data, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(data, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(data),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(data) ?? Array.Empty<string>(),
        _ => data,
    };
}

// -- Windows services ----------------------------------------------------------

public enum ServiceStart { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>A service's startup setting and whether it is running; null from <see cref="IServiceControl.Query"/> when it is not installed.</summary>
public sealed record ServiceSnapshot(ServiceStart Start, bool Running);

/// <summary>The service calls the framework needs, behind an interface so tests never touch real services.</summary>
public interface IServiceControl
{
    ServiceSnapshot? Query(string name);

    void SetStart(string name, ServiceStart start);

    void Stop(string name, TimeSpan timeout);

    void Start(string name, TimeSpan timeout);
}

/// <summary>
/// A service's startup type (including delayed start) and running state. Stopping a service ends
/// whatever it was doing; starting it again does not bring that back, so service changes are only
/// partially reversible.
/// </summary>
public sealed class ServiceChangeHandler : IChangeHandler
{
    public const string Kind = "service";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IServiceControl _services;

    public ServiceChangeHandler(IServiceControl? services = null)
    {
        _services = services ?? new WindowsServiceControl();
    }

    public IReadOnlyCollection<string> Kinds { get; } = new[] { Kind };

    public static ChangeRequest Set(string serviceName, ServiceStart start, bool running) =>
        new(Kind, serviceName, ChangeState.Of(("start", start.ToString()), ("running", running ? "true" : "false")));

    public ChangeState Read(string kind, string target) => _services.Query(target) is { } snapshot
        ? ChangeState.Of(("start", snapshot.Start.ToString()), ("running", snapshot.Running ? "true" : "false"))
        : ChangeState.Of(("installed", "false"));

    public void Apply(string kind, string target, ChangeState state)
    {
        if (state["installed"] == "false") throw new InvalidOperationException($"{target} is not installed.");
        var current = _services.Query(target) ?? throw new InvalidOperationException($"{target} is not installed.");
        var start = Enum.Parse<ServiceStart>(state["start"] ?? nameof(ServiceStart.Manual));
        bool running = state["running"] == "true";

        if (current.Start != start) _services.SetStart(target, start);
        if (current.Running && !running) _services.Stop(target, Timeout);
        else if (!current.Running && running) _services.Start(target, Timeout);
    }

    public string Describe(string kind, string target, ChangeState state)
    {
        if (state["installed"] == "false") return "not installed";
        var start = state["start"] switch
        {
            nameof(ServiceStart.AutomaticDelayed) => "Automatic (delayed start)",
            var other => other ?? "unknown",
        };
        return $"{start}, {(state["running"] == "true" ? "running" : "stopped")}";
    }

    public bool RequiresAdministrator(string kind, string target) => true;

    public (Reversibility Level, string? Note) ReversibilityOf(string kind, string target) =>
        (Reversibility.PartiallyReversible,
         "Its startup setting comes back exactly and it is started again if it was running, but whatever it was doing when it was stopped is not restored.");
}

/// <summary>The real services, through the Windows service control manager (no sc.exe).</summary>
public sealed class WindowsServiceControl : IServiceControl
{
    public ServiceSnapshot? Query(string name)
    {
        using var service = new ServiceController(name);
        try
        {
            var startType = service.StartType;
            bool running = service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
                or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.Paused or ServiceControllerStatus.PausePending;
            var start = startType switch
            {
                ServiceStartMode.Boot => ServiceStart.Boot,
                ServiceStartMode.System => ServiceStart.System,
                ServiceStartMode.Automatic => IsDelayed(name) ? ServiceStart.AutomaticDelayed : ServiceStart.Automatic,
                ServiceStartMode.Manual => ServiceStart.Manual,
                _ => ServiceStart.Disabled,
            };
            return new ServiceSnapshot(start, running);
        }
        catch (InvalidOperationException)
        {
            // ServiceController reports a missing service this way.
            return null;
        }
    }

    public void SetStart(string name, ServiceStart start)
    {
        uint startType = start switch
        {
            ServiceStart.Boot => 0,
            ServiceStart.System => 1,
            ServiceStart.Automatic or ServiceStart.AutomaticDelayed => 2,
            ServiceStart.Manual => 3,
            _ => 4,
        };

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, name, ServiceChangeConfig | ServiceQueryConfig);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!ChangeServiceConfig(service, ServiceNoChange, startType, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (start is ServiceStart.Automatic or ServiceStart.AutomaticDelayed)
                {
                    var info = new ServiceDelayedAutoStartInfo { DelayedAutostart = start == ServiceStart.AutomaticDelayed };
                    if (!ChangeServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public void Stop(string name, TimeSpan timeout)
    {
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void Start(string name, TimeSpan timeout)
    {
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    private static bool IsDelayed(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key?.GetValue("DelayedAutostart") is int delayed && delayed == 1;
        }
        catch (Exception ex) when (ExpectedFailures.Registry(ex))
        {
            return false;
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DelayedAutostart;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref ServiceDelayedAutoStartInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
