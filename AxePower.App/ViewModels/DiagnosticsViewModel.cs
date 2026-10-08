using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.App.Services;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;

namespace AxePower.App.ViewModels;

/// <summary>
/// Settings › Diagnostics: what AxePower has recorded about problems, how long it keeps it, and the
/// two things the user can do with it. Export only ever happens when the user saves a file; delete
/// removes every stored file. There is no upload anywhere in this flow.
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly IDiagnosticsPrompts _prompts;
    private readonly string? _uninstallLogDirectory;

    public DiagnosticsViewModel(LogService log, SettingsService settings, IDiagnosticsPrompts prompts, string? uninstallLogDirectory = null)
    {
        _log = log;
        _settings = settings;
        _prompts = prompts;
        _uninstallLogDirectory = uninstallLogDirectory ?? DiagnosticsBundleBuilder.DefaultUninstallLogDirectory;
        Refresh();
    }

    /// <summary>False when this session keeps its log in memory only (nothing on disk to show).</summary>
    public bool IsStored => _log.Store is not null;

    public string RetentionText => _log.Store is { } store
        ? $"Kept on this PC for {store.Retention.MaxAge.TotalDays:0} days or {FormatBytes(store.Retention.MaxTotalBytes)}, whichever comes first. Never uploaded."
        : "This session keeps its log in memory only.";

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasFiles;

    [RelayCommand]
    public void Refresh()
    {
        if (_log.Store is not { } store)
        {
            SummaryText = "Nothing stored";
            HasFiles = false;
            return;
        }

        var summary = store.Summarize();
        HasFiles = summary.Files > 0;
        SummaryText = summary.Files == 0
            ? "Nothing stored"
            : $"{summary.Files} file(s), {FormatBytes(summary.Bytes)}, since {summary.OldestUtc!.Value.ToLocalTime():d MMM yyyy}";
    }

    [RelayCommand]
    private void Inspect()
    {
        if (CreateBundle() is { } bundle) _prompts.ShowBundle(bundle);
        Refresh();
    }

    [RelayCommand]
    private void DeleteAll()
    {
        if (_log.Store is not { } store) return;
        var summary = store.Summarize();
        if (summary.Files == 0)
        {
            StatusText = "There is nothing to delete.";
            return;
        }
        if (!_prompts.ConfirmDeleteAll(summary)) return;

        var result = store.DeleteAll();
        _log.Logs.Clear();
        // Deliberately not logged: a "deleted" event would put a file straight back on disk.
        StatusText = result.Complete
            ? $"Deleted {result.Deleted} file(s)."
            : $"Deleted {result.Deleted} file(s); {result.Failed.Count} could not be removed because they are in use. Try again in a moment.";
        Refresh();
    }

    /// <summary>The inspect-and-export view of what is stored now, or null when nothing is stored.</summary>
    public DiagnosticsBundleViewModel? CreateBundle() =>
        _log.Store is { } store
            ? new DiagnosticsBundleViewModel(new DiagnosticsBundleBuilder(store, _settings.GetSettings, _uninstallLogDirectory), _log, _prompts)
            : null;

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB"),
    };
}

/// <summary>
/// The inspect-and-export window. <see cref="Items"/> is the complete bundle with each file's full
/// text; <see cref="SaveCommand"/> writes exactly those items, so what the user reviewed is what is saved.
/// </summary>
public partial class DiagnosticsBundleViewModel : ObservableObject
{
    private readonly DiagnosticsBundleBuilder _builder;
    private readonly LogService _log;
    private readonly IDiagnosticsPrompts _prompts;

    public DiagnosticsBundleViewModel(DiagnosticsBundleBuilder builder, LogService log, IDiagnosticsPrompts prompts)
    {
        _builder = builder;
        _log = log;
        _prompts = prompts;
        Rebuild();
    }

    public ObservableCollection<DiagnosticsBundleItem> Items { get; } = new();

    [ObservableProperty]
    private DiagnosticsBundleItem? _selectedItem;

    [ObservableProperty]
    private bool _includeSettings;

    [ObservableProperty]
    private bool _includeUninstallLogs;

    [ObservableProperty]
    private string _totalText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    partial void OnIncludeSettingsChanged(bool value) => Rebuild();

    partial void OnIncludeUninstallLogsChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        var selected = SelectedItem?.Name;
        Items.Clear();
        foreach (var item in _builder.Preview(new DiagnosticsBundleOptions(IncludeSettings, IncludeUninstallLogs))) Items.Add(item);
        SelectedItem = Items.FirstOrDefault(i => i.Name == selected) ?? Items.FirstOrDefault();
        TotalText = $"{Items.Count} file(s), {DiagnosticsViewModel.FormatBytes(Items.Sum(i => i.Bytes))} before compression";
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void Save()
    {
        var path = _prompts.ChooseBundlePath($"AxePower-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (path is null) return;

        try
        {
            var items = Items.ToList();
            DiagnosticsBundleBuilder.Write(items, path);
            _log.Log(LogLevels.Info, "Diagnostics", LogEvents.DiagnosticsExported,
                $"Saved a diagnostic bundle of {items.Count} file(s) at the user's request.");
            StatusText = $"Saved {Path.GetFileName(path)}. It is on this PC only; share it yourself if you want to.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText = AxePower.Core.Services.Outcomes.UserMessages.Failed("Save the bundle", ex).Text;
            _log.LogWarning("[Diagnostics] Saving a diagnostic bundle failed", ex);
        }
    }
}
