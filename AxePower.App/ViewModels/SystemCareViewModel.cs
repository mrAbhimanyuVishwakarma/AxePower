using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels;

/// <summary>
/// Hosts the optimizer and diagnostic tools under one System Care destination while preserving
/// each feature's existing view model and state.
/// </summary>
public partial class SystemCareViewModel : ObservableObject
{
    public SystemOptimizerViewModel OptimizerVm { get; }
    public SystemDoctorViewModel DoctorVm { get; }

    public IReadOnlyList<SystemCareCategoryItem> Categories { get; } = new[]
    {
        new SystemCareCategoryItem("All", "All Tweaks", "\uE71D", "OPTIMIZE"),
        new SystemCareCategoryItem("Privacy", "Privacy & Telemetry", "\uEA18"),
        new SystemCareCategoryItem("Gaming", "Gaming & Latency", "\uE7FC"),
        new SystemCareCategoryItem("Windows11", "Windows 11 QoL", "\uE713"),
        new SystemCareCategoryItem("Doctor", "Health & Repairs", "\uE90F", "DOCTOR & REPAIR"),
        new SystemCareCategoryItem("Cleaner", "Junk & Shader Cleaner", "\uE74D"),
        new SystemCareCategoryItem("Dns", "Low-Latency DNS", "\uE774"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptimizerActive))]
    [NotifyPropertyChangedFor(nameof(IsDoctorActive))]
    private string _activeSection = "Optimizer";

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private bool _isCategoryListCollapsed;

    public bool IsOptimizerActive => ActiveSection == "Optimizer";
    public bool IsDoctorActive => ActiveSection == "Doctor";

    public SystemCareViewModel(
        SystemOptimizerViewModel optimizerVm,
        SystemDoctorViewModel doctorVm)
    {
        OptimizerVm = optimizerVm;
        DoctorVm = doctorVm;
        Categories[0].IsSelected = true;
    }

    [RelayCommand]
    public void SelectSection(string? section)
    {
        SelectCategory(string.Equals(section, "Doctor", StringComparison.OrdinalIgnoreCase)
            ? "Doctor"
            : "All");
    }

    [RelayCommand]
    private void SelectCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;

        SelectedCategory = category;
        foreach (var item in Categories) item.IsSelected = item.Id == category;

        if (category is "Doctor" or "Cleaner" or "Dns")
        {
            ActiveSection = "Doctor";
            DoctorVm.SetTab(category);
        }
        else
        {
            ActiveSection = "Optimizer";
            OptimizerVm.SetCategory(category);
        }
    }

    [RelayCommand]
    private void ToggleCategoryList() => IsCategoryListCollapsed = !IsCategoryListCollapsed;
}

public partial class SystemCareCategoryItem : ObservableObject
{
    public SystemCareCategoryItem(string id, string title, string glyph, string? groupHeader = null)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        GroupHeader = groupHeader;
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string? GroupHeader { get; }

    [ObservableProperty]
    private bool _isSelected;
}
