using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.Models;

public partial class ToolInstructionItem : ObservableObject
{
    /// <summary>The guide open in the help window; its row in the tool list is highlighted.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public string? ImageIconSource { get; set; }
    public string Subtitle { get; set; } = string.Empty;
    public string Credit { get; set; } = string.Empty;
    public string CreditUrl { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public List<string> Tips { get; set; } = new();
    public string LaunchButtonText { get; set; } = "Launch Tool";
}
