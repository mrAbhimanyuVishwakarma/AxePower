using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.App.Services;
using AxePower.Core.Services.Operations;

namespace AxePower.App.ViewModels;

public partial class SystemDoctorViewModel : ObservableObject
{
    private readonly ISystemDoctorService _doctorService;
    private readonly ICacheCleanerService _cacheCleanerService;
    private readonly IConfirmationPrompts _confirm;
    private readonly IDnsOptimizerService _dnsService;
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;
    private readonly TerminalService _terminalService;

    private CancellationTokenSource? _diagnosticCts;

    [ObservableProperty]
    private string _activeTab = "Doctor"; // "Doctor", "Cleaner", "Dns"

    // Diagnostics & Health Repair
    [ObservableProperty]
    private string _consoleLogs = "AxePower System Doctor Ready.\nSelect a diagnostic tool above to begin repair & health scanning.\n";

    [ObservableProperty]
    private bool _isDiagnosticRunning;

    [ObservableProperty]
    private string _diagnosticStatusText = string.Empty;

    // Cache Cleaner
    public ObservableCollection<CacheCategorySummary> CacheCategories { get; } = new();

    [ObservableProperty]
    private bool _isScanningCache;

    [ObservableProperty]
    private bool _isCleaningCache;

    [ObservableProperty]
    private long _totalScannedBytes;

    [ObservableProperty]
    private string _formattedTotalScanned = "0 MB";

    [ObservableProperty]
    private string _cleanSummaryText = string.Empty;

    // DNS Optimizer
    public ObservableCollection<DnsProviderItem> DnsProviders { get; } = new();

    [ObservableProperty]
    private bool _isBenchmarkingDns;

    [ObservableProperty]
    private string _activeAdapterName = "Detecting...";

    [ObservableProperty]
    private string _dnsStatusText = string.Empty;

    public SystemDoctorViewModel(
        ISystemDoctorService doctorService,
        ICacheCleanerService cacheCleanerService,
        IDnsOptimizerService dnsService,
        NotificationService notificationService,
        LogService logService,
        TerminalService terminalService,
        IConfirmationPrompts? confirm = null)
    {
        _confirm = confirm ?? new ConfirmationPrompts();
        _doctorService = doctorService;
        _cacheCleanerService = cacheCleanerService;
        _dnsService = dnsService;
        _notificationService = notificationService;
        _logService = logService;
        _terminalService = terminalService;

        LoadDnsProviders();
        _ = InitializeDataAsync();
    }

    private async Task InitializeDataAsync()
    {
        await AnalyzeCacheAsync();
        ActiveAdapterName = await _dnsService.GetActiveAdapterNameAsync();
    }

    [RelayCommand]
    public void SetTab(string tabName)
    {
        ActiveTab = tabName;
    }

    #region Diagnostics Hub (SFC, DISM, WinGet, Network)

    [RelayCommand]
    public async Task RunSfcScanAsync()
    {
        await ExecuteDiagnosticTask(DiagnosticTaskType.SfcScan, "System File Checker (SFC)");
    }

    [RelayCommand]
    public async Task RunDismRepairAsync()
    {
        await ExecuteDiagnosticTask(DiagnosticTaskType.DismRepair, "DISM Component Store Repair");
    }

    [RelayCommand]
    public async Task RunWinGetResetAsync()
    {
        await ExecuteDiagnosticTask(DiagnosticTaskType.WinGetReset, "WinGet Catalog Reset");
    }

    [RelayCommand]
    public async Task RunNetworkResetAsync()
    {
        await ExecuteDiagnosticTask(DiagnosticTaskType.NetworkReset, "Network & Winsock Reset");
    }

    [RelayCommand]
    public void CancelDiagnostic()
    {
        if (IsDiagnosticRunning && _diagnosticCts != null)
        {
            _diagnosticCts.Cancel();
            _notificationService.Show("Cancelling diagnostic task...");
        }
    }

    [RelayCommand]
    public void ClearConsole()
    {
        ConsoleLogs = "Console output cleared.\n";
    }

    private async Task ExecuteDiagnosticTask(DiagnosticTaskType taskType, string taskName)
    {
        if (IsDiagnosticRunning) return;

        IsDiagnosticRunning = true;
        DiagnosticStatusText = $"Running {taskName}...";
        _diagnosticCts = new CancellationTokenSource();

        // Diagnostic output streams to the shared Terminal page; jump there and let its Stop
        // button cancel this task while the buttons here are off-screen.
        _terminalService.ActiveExternalCancel = () => _diagnosticCts?.Cancel();
        _terminalService.AppendExternalLine($"\n[{DateTime.Now:HH:mm:ss}] >>> Starting {taskName} <<<");
        _terminalService.RequestNavigateToTerminal();

        var sb = new StringBuilder(ConsoleLogs);
        sb.AppendLine($"\n[{DateTime.Now:HH:mm:ss}] >>> Starting {taskName} <<<");
        ConsoleLogs = sb.ToString();

        var progress = new Progress<string>(line =>
        {
            _terminalService.AppendExternalLine(line);
            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                ConsoleLogs += line + "\n";
            });
        });

        try
        {
            bool success = await _doctorService.RunDiagnosticTaskAsync(taskType, progress, _diagnosticCts.Token);
            if (success)
            {
                _notificationService.Show($"✓ {taskName} completed successfully!");
            }
            else
            {
                _notificationService.Show($"Notice: {taskName} finished with warnings or cancellation.");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogs += $"[EXCEPTION] {AxePower.Core.Services.Outcomes.OutcomeClassifier.Detail(ex)}\n";
            _notificationService.ShowFailure($"Run {taskName}", ex);
        }
        finally
        {
            IsDiagnosticRunning = false;
            DiagnosticStatusText = string.Empty;
            _terminalService.ActiveExternalCancel = null;
            _diagnosticCts?.Dispose();
            _diagnosticCts = null;
        }
    }

    #endregion

    #region Cache & Shader Cleaner

    [RelayCommand]
    public async Task AnalyzeCacheAsync()
    {
        if (IsScanningCache || IsCleaningCache) return;

        IsScanningCache = true;
        try
        {
            var summaries = await _cacheCleanerService.AnalyzeCachesAsync();
            CacheCategories.Clear();

            long total = 0;
            foreach (var item in summaries)
            {
                CacheCategories.Add(item);
                total += item.TotalSizeBytes;
            }

            TotalScannedBytes = total;
            FormattedTotalScanned = CacheCategorySummary.FormatBytes(total);
            CleanSummaryText = $"Found {FormattedTotalScanned} across {CacheCategories.Count} cache locations.";
        }
        finally
        {
            IsScanningCache = false;
        }
    }

    [RelayCommand]
    public async Task CleanCacheAsync()
    {
        if (IsCleaningCache || IsScanningCache) return;

        IsCleaningCache = true;
        try
        {
            _notificationService.Show("Cleaning selected junk & shader caches...");
            var result = await _cacheCleanerService.CleanCachesAsync(CacheCategories);

            _notificationService.Show($"✓ Cleaned {result.FormattedCleanedSize} ({result.FilesDeleted} files deleted)!");
            CleanSummaryText = $"Successfully freed {result.FormattedCleanedSize} ({result.FilesDeleted} deleted, {result.FilesSkipped} in-use skipped).";

            // Re-analyze after clean
            await AnalyzeCacheAsync();
        }
        finally
        {
            IsCleaningCache = false;
        }
    }

    #endregion

    #region Low-Latency DNS Optimizer

    private void LoadDnsProviders()
    {
        DnsProviders.Clear();
        foreach (var p in _dnsService.GetAvailableProviders())
        {
            DnsProviders.Add(p);
        }
    }

    [RelayCommand]
    public async Task BenchmarkDnsAsync()
    {
        if (IsBenchmarkingDns) return;

        IsBenchmarkingDns = true;
        DnsStatusText = "Pinging DNS providers to benchmark latency...";
        try
        {
            var results = await _dnsService.BenchmarkDnsProvidersAsync();
            DnsProviders.Clear();
            foreach (var r in results)
            {
                DnsProviders.Add(r);
            }

            var fastest = results.FirstOrDefault(r => r.IsFastest);
            if (fastest != null)
            {
                DnsStatusText = $"Benchmark complete! Fastest provider: {fastest.Name} ({fastest.FormattedLatency}).";
                _notificationService.Show($"Fastest DNS: {fastest.Name} ({fastest.FormattedLatency})");
            }
            else
            {
                DnsStatusText = "Benchmark complete.";
            }
        }
        finally
        {
            IsBenchmarkingDns = false;
        }
    }

    [RelayCommand]
    public async Task ApplyDnsAsync(DnsProviderItem provider)
    {
        if (provider == null) return;
        var adapter = await _dnsService.GetActiveAdapterNameAsync();
        var servers = string.Join(", ", new[] { provider.PrimaryIpv4, provider.SecondaryIpv4 }.Where(a => !string.IsNullOrWhiteSpace(a)));
        if (!_confirm.Confirm(ConfirmationCatalog.ChangeDns(adapter, provider.Name, servers, await Task.Run(() => DnsCurrent.Read(adapter))))) return;

        _notificationService.Show($"Setting DNS to {provider.Name}...");
        bool success = await _dnsService.ApplyDnsProviderAsync(provider.Id);

        if (success)
        {
            foreach (var p in DnsProviders)
            {
                p.IsActive = (p.Id == provider.Id);
            }
            DnsStatusText = $"Active DNS: {provider.Name}";
            _notificationService.Show($"✓ Switched to {provider.Name} and flushed DNS cache!");
        }
        else
        {
            _notificationService.Show($"Failed to apply {provider.Name}. Check admin permissions.");
        }
    }

    [RelayCommand]
    public async Task ResetDhcpAsync()
    {
        var adapter = await _dnsService.GetActiveAdapterNameAsync();
        if (!_confirm.Confirm(ConfirmationCatalog.ResetDnsToAutomatic(adapter, await Task.Run(() => DnsCurrent.Read(adapter))))) return;

        _notificationService.Show("Resetting DNS to Automatic (DHCP)...");
        bool success = await _dnsService.ResetToDhcpAsync();

        if (success)
        {
            foreach (var p in DnsProviders)
            {
                p.IsActive = p.IsDhcp;
            }
            DnsStatusText = "Active DNS: Automatic (DHCP)";
            _notificationService.Show("✓ Restored Automatic DHCP DNS!");
        }
        else
        {
            _notificationService.Show("Failed to reset DNS to DHCP.");
        }
    }

    #endregion
}
