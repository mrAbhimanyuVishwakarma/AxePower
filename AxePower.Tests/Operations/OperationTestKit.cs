using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AxePower.Core.Services;
using AxePower.Core.Services.Operations;
using AxePower.Tests.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Tests.Operations;

/// <summary>
/// A scratch registry (HKLM and HKCU both mapped under a throwaway HKCU key), a temporary journal
/// and a fake service manager, so operation tests never touch the real machine.
/// </summary>
internal sealed class OperationTestKit : IDisposable
{
    private readonly TestRegistryKey _registry = new();
    private readonly Dictionary<RegistryHive, RegistryKey> _roots = new();

    public OperationTestKit(bool elevated = true)
    {
        Directory.CreateDirectory(JournalDirectory);
        Elevated = elevated;
        Registry = new RegistryChangeHandler(Root);
        Runner = new OperationRunner(new OperationJournal(JournalDirectory), Log,
            new IChangeHandler[] { Registry, new ServiceChangeHandler(Services) }, () => Elevated, () => Now);
    }

    public string JournalDirectory { get; } = Path.Combine(Path.GetTempPath(), "AxePower_Ops_" + Guid.NewGuid().ToString("N"));
    public LogService Log { get; } = new();
    public FakeServices Services { get; } = new();
    public RegistryChangeHandler Registry { get; }
    public OperationRunner Runner { get; }
    public bool Elevated { get; set; }
    public DateTime Now { get; set; } = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    public RegistryKey Root(RegistryHive hive)
    {
        if (!_roots.TryGetValue(hive, out var key))
        {
            key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{_registry.SubKey}\{hive}", writable: true);
            _roots[hive] = key;
        }
        return key;
    }

    /// <summary>Every key and value under the scratch registry, for before/after comparisons.</summary>
    public string Dump()
    {
        var lines = new List<string>();
        void Walk(RegistryKey key, string path)
        {
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var text = value is string[] array ? string.Join("\\0", array) : value is byte[] bytes ? Convert.ToBase64String(bytes) : value?.ToString();
                lines.Add($"{path}|{name}={key.GetValueKind(name)}:{text}");
            }
            foreach (var sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                lines.Add($"{path}\\{sub}\\");
                using var child = key.OpenSubKey(sub)!;
                Walk(child, $"{path}\\{sub}");
            }
        }
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser }) Walk(Root(hive), hive.ToString());
        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        foreach (var key in _roots.Values) key.Dispose();
        _registry.Dispose();
        try { Directory.Delete(JournalDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A pretend service control manager.</summary>
internal sealed class FakeServices : IServiceControl
{
    public Dictionary<string, ServiceSnapshot> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RefuseToStop { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = new();

    public ServiceSnapshot? Query(string name) => Installed.TryGetValue(name, out var s) ? s : null;

    public void SetStart(string name, ServiceStart start)
    {
        Calls.Add($"start {name} {start}");
        Installed[name] = Installed[name] with { Start = start };
    }

    public void Stop(string name, TimeSpan timeout)
    {
        Calls.Add($"stop {name}");
        if (RefuseToStop.Contains(name)) throw new InvalidOperationException($"Cannot stop {name} service on computer '.'.");
        Installed[name] = Installed[name] with { Running = false };
    }

    public void Start(string name, TimeSpan timeout)
    {
        Calls.Add($"run {name}");
        Installed[name] = Installed[name] with { Running = true };
    }
}
