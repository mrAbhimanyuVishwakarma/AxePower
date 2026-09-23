using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// A file system that only contains the files the test lists. Files under the Windows folder
/// (msiexec.exe, rundll32.exe…) are looked up on the real disk, read-only, so bare-name
/// resolution behaves as it does in the app.
/// </summary>
internal sealed class FakeFileSystemProbe : IFileSystemProbe
{
    private static readonly string WindowsFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public FakeFileSystemProbe(params string[] existing)
    {
        foreach (var path in existing) Existing.Add(Path.GetFullPath(path));
    }

    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose content contains "Nullsoft".</summary>
    public HashSet<string> NullsoftFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddFile(string path) => Existing.Add(Path.GetFullPath(path));

    public bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.GetFullPath(path);
        if (Existing.Contains(full)) return true;
        return full.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
    }

    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directories.Contains(Path.GetFullPath(path));

    public bool FileContainsAscii(string path, string text, long maxBytes) =>
        text == "Nullsoft" && NullsoftFiles.Contains(Path.GetFullPath(path));
}

/// <summary>MSI product states by ProductCode; unknown codes report "installed" (5) unless listed.</summary>
internal sealed class FakeMsiReader : IMsiProductStateReader
{
    public Dictionary<string, int> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int DefaultState { get; set; } = MsiProductStateReader.InstallStateDefault;

    public int QueryProductState(string productCode) =>
        States.TryGetValue(productCode, out var state) ? state : DefaultState;
}

/// <summary>A verifier whose answers are scripted by the test.</summary>
internal sealed class FakeVerifier : IInstallStateVerifier
{
    private readonly HashSet<string> _preChecked = new();

    /// <summary>Answer to each app's pre-check (its first IsRegistered call).</summary>
    public bool Registered { get; set; } = true;

    /// <summary>Answer to every check after the uninstaller ran.</summary>
    public bool StillInstalledAfterRun { get; set; }

    public Task<bool> IsStillInstalledAsync(InstalledApplication app, CancellationToken cancellationToken) =>
        Task.FromResult(StillInstalledAfterRun);

    public Task<bool> IsRegisteredAsync(InstalledApplication app, CancellationToken cancellationToken) =>
        Task.FromResult(_preChecked.Add(app.Id) ? Registered : StillInstalledAfterRun);

    public Task<bool> WaitUntilRemovedAsync(InstalledApplication app, TimeSpan timeout, CancellationToken cancellationToken) =>
        IsStillInstalledAsync(app, cancellationToken);
}

/// <summary>An executor that returns a scripted execution and never starts a process.</summary>
internal sealed class FakeExecutor : IUninstallExecutor
{
    private readonly Func<InstalledApplication, UninstallExecution> _result;

    public FakeExecutor(Func<InstalledApplication, UninstallExecution> result, params UninstallerKind[] kinds)
    {
        _result = result;
        SupportedKinds = kinds.Length > 0 ? kinds : Enum.GetValues<UninstallerKind>();
    }

    public IReadOnlyCollection<UninstallerKind> SupportedKinds { get; }
    public List<string> Executed { get; } = new();
    public Func<UninstallRunContext, CancellationToken, Task>? BeforeReturn { get; set; }

    public async Task<UninstallExecution> ExecuteAsync(InstalledApplication application, UninstallerSettings settings,
        UninstallRunContext context, CancellationToken cancellationToken = default)
    {
        Executed.Add(application.DisplayName);
        if (BeforeReturn is not null) await BeforeReturn(context, cancellationToken);
        return _result(application);
    }
}

internal sealed class FakeScanner : ILeftoverScannerService
{
    public List<string> Scanned { get; } = new();
    public LeftoverScanOptions? LastOptions { get; private set; }

    public Task<IReadOnlyList<LeftoverItem>> ScanAsync(InstalledApplication application, ScanMode scanMode,
        UninstallerSettings settings, LeftoverScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        Scanned.Add(application.DisplayName);
        LastOptions = options;
        IReadOnlyList<LeftoverItem> items =
        [
            new LeftoverItem { ApplicationId = application.Id, ApplicationName = application.DisplayName, Path = @"C:\Fake\" + application.DisplayName },
        ];
        return Task.FromResult(items);
    }
}

internal sealed class FakeDiscovery : IApplicationDiscoveryService
{
    public List<InstalledApplication> Apps { get; } = new();

    public Task<IReadOnlyList<InstalledApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstalledApplication>>(Apps);

    public Task<IReadOnlyList<InstalledApplication>> DiscoverRegistryApplicationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstalledApplication>>(Apps);
}

internal sealed class InMemoryHistory : IUninstallHistoryService
{
    public List<UninstallSession> Sessions { get; } = new();

    public Task<IReadOnlyList<UninstallSession>> GetSessionsAsync() => Task.FromResult<IReadOnlyList<UninstallSession>>(Sessions);

    public Task SaveSessionAsync(UninstallSession session)
    {
        Sessions.RemoveAll(s => s.SessionId == session.SessionId);
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId)
    {
        Sessions.RemoveAll(s => s.SessionId == sessionId);
        return Task.CompletedTask;
    }

    public Task ClearAllSessionsAsync()
    {
        Sessions.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class NoRunningProcesses : IProcessDetectionService
{
    public List<RunningProcessInfo> Running { get; } = new();
    public bool CloseSucceeds { get; set; } = true;

    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses(InstalledApplication application) => Running;
    public bool IsApplicationRunning(InstalledApplication application) => Running.Count > 0;

    public Task<bool> TryCloseApplicationAsync(InstalledApplication application, TimeSpan timeout, bool forceKill = false)
    {
        if (CloseSucceeds) Running.Clear();
        return Task.FromResult(CloseSucceeds);
    }
}

/// <summary>Scripted answers to the batch's questions.</summary>
internal sealed class FakeInteraction : IUninstallInteraction
{
    public Queue<RunningAppDecision> RunningAnswers { get; } = new();
    public bool StopStalled { get; set; }

    public Task<RunningAppDecision> ResolveRunningApplicationAsync(InstalledApplication app,
        IReadOnlyList<RunningProcessInfo> processes, bool closeAttemptFailed, CancellationToken cancellationToken) =>
        Task.FromResult(RunningAnswers.Count > 0 ? RunningAnswers.Dequeue() : RunningAppDecision.Skip);

    public Task<bool> ShouldStopStalledUninstallerAsync(InstalledApplication app, TimeSpan elapsed, CancellationToken cancellationToken) =>
        Task.FromResult(StopStalled);
}

/// <summary>A disposable HKCU\Software\AxePower.Tests\{guid} key, deleted on dispose.</summary>
internal sealed class TestRegistryKey : IDisposable
{
    public TestRegistryKey()
    {
        SubKey = $@"Software\AxePower.Tests\{Guid.NewGuid():N}";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
    }

    /// <summary>Below HKCU, e.g. Software\AxePower.Tests\abc…</summary>
    public string SubKey { get; }

    /// <summary>HKCU\Software\AxePower.Tests\abc…</summary>
    public string FullPath => $@"HKCU\{SubKey}";

    public void Dispose()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(SubKey, throwOnMissingSubKey: false);
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\AxePower.Tests", writable: true);
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\AxePower.Tests", throwOnMissingSubKey: false);
        }
        catch { }
    }
}

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxePower.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateFile(string relative, string content = "x")
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
