using System.Collections.Generic;

namespace AxePower.App.Models;

public class DebloatToolItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LaunchButtonTitle { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string CreditLabel => string.IsNullOrWhiteSpace(Author) ? string.Empty : $"by {Author}";
    public string CreditUrl { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE756";
    public string Tagline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Highlights { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string CommandPreview { get; set; } = string.Empty;
    public string TooltipText => $"Credit: {CreditLabel} (Click to open repository)\nOfficial Source: {CreditUrl}";
}
