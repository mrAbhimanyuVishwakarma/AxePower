using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services;

public sealed record WindowsUpdateCandidate(
    string Title,
    string Detail,
    bool IsDeviceUpdate,
    Guid? UpdateId = null,
    int? RevisionNumber = null,
    bool IsOptional = false,
    bool? IsFirmware = null);

public sealed record WindowsUpdateScanResult(
    IReadOnlyList<WindowsUpdateCandidate> Candidates,
    bool Succeeded,
    bool? RebootRequired,
    string? Detail = null);

public interface IWindowsUpdateCatalogService
{
    Task<WindowsUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the update catalog selected by Windows and the Windows Update restart state.</summary>
public sealed class WindowsUpdateCatalogService : IWindowsUpdateCatalogService
{
    private const string SearchCriteria = "IsInstalled=0 and IsHidden=0";
    private const int SucceededResult = 2;
    private const int PartialResult = 3;
    private const int DriverUpdateType = 2;
    private static readonly TimeSpan ScanWait = TimeSpan.FromMinutes(3);

    private readonly LogService _log;
    private readonly Func<WindowsUpdateScanResult> _scan;
    private readonly object _gate = new();
    private Task<WindowsUpdateScanResult>? _inFlight;

    public WindowsUpdateCatalogService(LogService log)
    {
        _log = log;
        _scan = ReadWindowsUpdate;
    }

    internal WindowsUpdateCatalogService(LogService log, Func<WindowsUpdateScanResult> scan)
    {
        _log = log;
        _scan = scan;
    }

    public async Task<WindowsUpdateScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<WindowsUpdateScanResult> scan;
        lock (_gate)
        {
            if (_inFlight is null || _inFlight.IsCompleted)
                _inFlight = Task.Run(_scan);
            scan = _inFlight;
        }

        // Search is synchronous. Each caller can stop waiting without releasing COM objects
        // beneath the worker or starting a second search while the first is still in Windows.
        try
        {
            return await scan.WaitAsync(ScanWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning("[Windows Update] Timed out waiting for the Windows Update Agent scan.");
            return new([], false, null, "Windows Update is taking longer than expected. Try again or open Windows Update in Settings.");
        }
    }

    private WindowsUpdateScanResult ReadWindowsUpdate()
    {
        var candidates = new List<WindowsUpdateCandidate>();
        object? session = null;
        object? searcher = null;
        object? result = null;
        object? updates = null;

        try
        {
            // Leave ServerSelection and ServiceID unset. Windows chooses the configured source.
            // https://learn.microsoft.com/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search
            session = Create("Microsoft.Update.Session");
            searcher = Call(session, "CreateUpdateSearcher");
            result = Call(searcher, "Search", SearchCriteria);
            int resultCode = Read<int>(result, "ResultCode");
            if (resultCode is not (SucceededResult or PartialResult))
            {
                _log.LogWarning($"[Windows Update] Scan returned operation result {resultCode}.");
                return new([], false, ReadRestartRequired(), "Windows Update did not complete the scan. Try Windows Update in Settings.");
            }

            updates = Read<object>(result, "Updates");
            int count = Read<int>(updates, "Count");
            for (int index = 0; index < count; index++)
            {
                object item = Read<object>(updates, "Item", index);
                try
                {
                    candidates.Add(ReadCandidate(item));
                }
                finally
                {
                    Release(item);
                }
            }

            bool success = resultCode == SucceededResult;
            bool? reboot = ReadRestartRequired();
            if (!success)
                _log.LogWarning($"[Windows Update] Scan returned partial results ({candidates.Count} updates).");
            return new(candidates.AsReadOnly(), success, reboot,
                success ? null : "Windows Update returned partial results. Check Windows Update in Settings for the full list.");
        }
        catch (Exception ex) when (IsCatalogFailure(ex))
        {
            _log.LogError("[Windows Update] Could not read the Windows Update catalog.", ex);
            return new(candidates.AsReadOnly(), false, ReadRestartRequired(),
                "Windows Update could not complete the scan. Open Windows Update in Settings for details.");
        }
        finally
        {
            Release(updates);
            Release(result);
            Release(searcher);
            Release(session);
        }
    }

    private static WindowsUpdateCandidate ReadCandidate(object update)
    {
        string title = Read<string>(update, "Title");
        bool isDeviceUpdate = Read<int>(update, "Type") == DriverUpdateType;
        bool? isFirmware = isDeviceUpdate ? TryReadIsFirmware(update) : null;
        bool isOptional = TryReadOptional(update);
        object identity = Read<object>(update, "Identity");
        try
        {
            bool hasId = Guid.TryParse(Read<string>(identity, "UpdateID"), out Guid id);
            int revision = Read<int>(identity, "RevisionNumber");
            string detail = isFirmware == true ? "Firmware update" : isDeviceUpdate ? "Device update" : "Windows Update catalog";
            if (isOptional) detail = $"{detail} · Optional";
            string? articles = TryReadArticleIds(update);
            if (articles is not null) detail = $"{detail} · {articles}";

            return new(string.IsNullOrWhiteSpace(title) ? "Untitled Windows update" : title,
                detail, isDeviceUpdate, hasId ? id : null, revision, isOptional, isFirmware);
        }
        finally
        {
            Release(identity);
        }
    }

    private static bool? TryReadIsFirmware(object update)
    {
        try
        {
            // WUA exposes the device setup class on IWindowsDriverUpdate.
            // https://learn.microsoft.com/windows/win32/api/wuapi/nf-wuapi-iwindowsdriverupdate-get_driverclass
            string driverClass = Read<string>(update, "DriverClass").Trim();
            return driverClass.Length == 0 ? null : driverClass.Equals("Firmware", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsCatalogFailure(ex))
        {
            // A missing driver class leaves the device update unclassified.
            return null;
        }
    }

    private static string? TryReadArticleIds(object update)
    {
        object? articles = null;
        try
        {
            articles = Read<object>(update, "KBArticleIDs");
            int count = Read<int>(articles, "Count");
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string id = Read<string>(articles, "Item", i).Trim();
                if (id.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) id = id[2..];
                if (id.Length > 0 && id.All(char.IsAsciiDigit)) identifiers.Add($"KB{id}");
            }

            return identifiers.Count == 0 ? null : string.Join(", ", identifiers);
        }
        catch (Exception ex) when (IsCatalogFailure(ex))
        {
            // KB identifiers are display metadata. A driver or catalog may omit them.
            return null;
        }
        finally
        {
            Release(articles);
        }
    }

    private static bool TryReadOptional(object update)
    {
        try
        {
            // BrowseOnly is declared on IUpdate3 / IWindowsDriverUpdate3. Earlier
            // dispatch interfaces may expose only the base properties.
            return Read<bool>(update, "BrowseOnly");
        }
        catch (Exception ex) when (IsMissingMember(ex))
        {
            return false;
        }
    }

    private bool? ReadRestartRequired()
    {
        object? system = null;
        try
        {
            // https://learn.microsoft.com/windows/win32/api/wuapi/nn-wuapi-isysteminformation
            system = Create("Microsoft.Update.SystemInfo");
            return Read<bool>(system, "RebootRequired");
        }
        catch (Exception ex) when (IsCatalogFailure(ex))
        {
            _log.LogWarning($"[Windows Update] Restart status is unavailable: {ex.GetBaseException().Message}");
            return null;
        }
        finally
        {
            Release(system);
        }
    }

    private static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)
            ?? throw new InvalidOperationException("Windows Update Agent is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows Update Agent could not be initialized.");
    }

    private static object Call(object target, string name, params object[] arguments) =>
        Dispatch(target, name, BindingFlags.InvokeMethod, arguments);

    private static T Read<T>(object target, string name, params object[] arguments) =>
        (T)Dispatch(target, name, BindingFlags.GetProperty, arguments);

    private static object Dispatch(object target, string name, BindingFlags flags, object[] arguments) =>
        target.GetType().InvokeMember(name, flags, binder: null, target, arguments, CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"Windows Update Agent did not return {name}.");

    private static bool IsCatalogFailure(Exception ex) => ex.GetBaseException() is
        COMException or InvalidOperationException or MissingMemberException or InvalidCastException
        or UnauthorizedAccessException or PlatformNotSupportedException;

    private static bool IsMissingMember(Exception ex) => ex.GetBaseException() is
        MissingMemberException or COMException { HResult: unchecked((int)0x80020003) or unchecked((int)0x80020006) };

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}
