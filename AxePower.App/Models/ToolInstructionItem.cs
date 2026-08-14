using System.Collections.Generic;

namespace AxePower.App.Models;

public class ToolInstructionItem
{
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
