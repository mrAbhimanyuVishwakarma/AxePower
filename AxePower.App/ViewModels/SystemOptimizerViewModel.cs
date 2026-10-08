using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.Core.Services.Operations;
using AxePower.App.Services;

namespace AxePower.App.ViewModels;

public partial class TweakCardViewModel : ObservableObject
{
    private readonly ITweakService _tweakService;
    private readonly SystemOptimizerViewModel _parent;

    public SystemTweakItem Item { get; }

    public string Id => Item.Id;
    public string Title => Item.Title;
    public string Description => Item.Description;
    public string CategoryDisplayName => Item.CategoryDisplayName;
    public TweakCategory Category => Item.Category;
    public bool IsRecommended => Item.IsRecommended;
    public TweakImpact Impact => Item.Impact;

    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private bool _isBusy;

    public TweakCardViewModel(SystemTweakItem item, ITweakService tweakService, SystemOptimizerViewModel parent)
    {
        Item = item;
        _tweakService = tweakService;
        _parent = parent;
        _isApplied = item.IsApplied;
    }

    [RelayCommand]
    public async Task ToggleAsync()
    {
        if (IsBusy) return;

        // Preview first: nothing changes unless the user approves the exact changes.
        var plan = IsApplied ? _tweakService.PlanRevert(Id) : _tweakService.PlanApply(Id);
        if (!_parent.Prompts.Approve(new[] { plan })) return;

        IsBusy = true;
        try
        {
            var result = await _tweakService.ExecuteAsync(plan);
            IsApplied = Item.IsApplied;
            _parent.NotifyStatusChange(ReversibilityText.Outcome(result));
        }
        finally
        {
            IsBusy = false;
            _parent.UpdateCounts();
        }
    }

    public void SyncState()
    {
        IsApplied = Item.IsApplied;
    }
}

public partial class SystemOptimizerViewModel : ObservableObject
{
    private readonly ITweakService _tweakService;
    private readonly SystemRestoreService _systemRestoreService;
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;

    /// <summary>The preview every change goes through before it is applied.</summary>
    public IOperationPrompts Prompts { get; }

    public ObservableCollection<TweakCardViewModel> AllTweaks { get; } = new();
    public ObservableCollection<TweakCardViewModel> FilteredTweaks { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyStatusText = string.Empty;

    [ObservableProperty]
    private int _appliedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _recommendedCount;

    public SystemOptimizerViewModel(
        ITweakService tweakService,
        SystemRestoreService systemRestoreService,
        NotificationService notificationService,
        LogService logService,
        IOperationPrompts? prompts = null)
    {
        Prompts = prompts ?? new OperationPrompts();
        _tweakService = tweakService;
        _systemRestoreService = systemRestoreService;
        _notificationService = notificationService;
        _logService = logService;

        LoadTweaks();
    }

    public void LoadTweaks()
    {
        AllTweaks.Clear();
        var items = _tweakService.GetAllTweaks();
        foreach (var item in items)
        {
            AllTweaks.Add(new TweakCardViewModel(item, _tweakService, this));
        }

        TotalCount = AllTweaks.Count;
        RecommendedCount = AllTweaks.Count(t => t.IsRecommended);

        ApplyFilter();
        _ = RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyStatusText = "Reading live Windows registry states...";
        try
        {
            await _tweakService.RefreshAllTweakStatusesAsync();
            foreach (var card in AllTweaks)
            {
                card.SyncState();
            }
            UpdateCounts();
        }
        finally
        {
            IsBusy = false;
            BusyStatusText = string.Empty;
        }
    }

    [RelayCommand]
    public async Task ApplyAllRecommendedAsync()
    {
        if (IsBusy) return;

        var plans = _tweakService.PlanApplyRecommended();
        if (plans.Count == 0)
        {
            _notificationService.ShowNotification("All recommended optimizations are already applied.");
            return;
        }
        if (!Prompts.Approve(plans)) return;

        IsBusy = true;
        BusyStatusText = "Creating System Restore Point...";
        try
        {
            // A restore point as well, for safety beyond what the change history can undo
            bool restoreCreated = await _systemRestoreService.TryCreateRestorePointAsync("AxePower_PreOptimization");
            if (restoreCreated)
            {
                _notificationService.ShowNotification("System Restore Point created successfully.");
            }

            BusyStatusText = "Applying recommended optimizations...";
            int applied = 0;
            foreach (var plan in plans)
            {
                if ((await _tweakService.ExecuteAsync(plan)).Status == OperationStatus.Succeeded) applied++;
            }
            await _tweakService.RefreshAllTweakStatusesAsync();

            foreach (var card in AllTweaks)
            {
                card.SyncState();
            }
            UpdateCounts();

            _notificationService.ShowNotification(applied == plans.Count
                ? $"Applied {applied} recommended optimizations. Undo them from Settings › Change history."
                : $"Applied {applied} of {plans.Count} recommended optimizations; see Settings › Change history for the rest.");
        }
        finally
        {
            IsBusy = false;
            BusyStatusText = string.Empty;
        }
    }

    [RelayCommand]
    public async Task RevertAllAsync()
    {
        if (IsBusy) return;

        var plans = _tweakService.PlanRevertAll();
        if (plans.Count == 0) return;
        if (!Prompts.Approve(plans)) return;

        IsBusy = true;
        BusyStatusText = "Reverting optimizations...";
        try
        {
            int reverted = 0;
            foreach (var plan in plans)
            {
                if ((await _tweakService.ExecuteAsync(plan)).Status == OperationStatus.Succeeded) reverted++;
            }
            await _tweakService.RefreshAllTweakStatusesAsync();

            foreach (var card in AllTweaks)
            {
                card.SyncState();
            }
            UpdateCounts();

            _notificationService.ShowNotification($"Reverted {reverted} of {plans.Count} tweak(s).");
        }
        finally
        {
            IsBusy = false;
            BusyStatusText = string.Empty;
        }
    }

    [RelayCommand]
    public async Task CreateRestorePointAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyStatusText = "Creating manual System Restore Point...";
        try
        {
            bool success = await _systemRestoreService.TryCreateRestorePointAsync("AxePower_Manual_Checkpoint");
            if (success)
            {
                _notificationService.ShowNotification("System Restore Point created successfully.");
            }
            else
            {
                _notificationService.ShowNotification("Restore Point unavailable (System Protection may be disabled).");
            }
        }
        finally
        {
            IsBusy = false;
            BusyStatusText = string.Empty;
        }
    }

    [RelayCommand]
    public void SetCategory(string category)
    {
        SelectedCategory = category;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredTweaks.Clear();

        var query = AllTweaks.AsEnumerable();

        if (!string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = SelectedCategory switch
            {
                "Privacy" => query.Where(t => t.Category == TweakCategory.Privacy),
                "Gaming" => query.Where(t => t.Category == TweakCategory.Gaming),
                "Windows11" => query.Where(t => t.Category == TweakCategory.Windows11),
                _ => query
            };
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(t => 
                t.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredTweaks.Add(item);
        }
    }

    public void UpdateCounts()
    {
        AppliedCount = AllTweaks.Count(t => t.IsApplied);
    }

    public void NotifyStatusChange(string message)
    {
        _notificationService.ShowNotification(message);
    }
}
