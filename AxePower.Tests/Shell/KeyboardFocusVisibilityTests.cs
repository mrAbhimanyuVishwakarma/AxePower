using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>
/// Keyboard focus stays visible (Prompt 28). A style or control that switches off WPF's dotted
/// focus rectangle (FocusVisualStyle = {x:Null}) must draw its own focus state, with a trigger
/// on IsKeyboardFocused, IsFocused or IsKeyboardFocusWithin.
/// </summary>
public class KeyboardFocusVisibilityTests
{
    private static readonly string[] FocusProperties = { "IsKeyboardFocused", "IsFocused", "IsKeyboardFocusWithin" };

    private static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found"), "AxePower.App");
    }

    private static bool DrawsFocus(XElement scope) =>
        scope.Descendants().Any(e => e.Name.LocalName is "Trigger" or "MultiTrigger" or "Condition"
                                     && FocusProperties.Contains((string?)e.Attribute("Property")));

    [Fact]
    public void EveryControlThatHidesTheFocusRectangle_DrawsItsOwnFocusState()
    {
        var offenders = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.GetFiles(AppFolder(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);

            // <Setter Property="FocusVisualStyle" Value="{x:Null}"/> inside a style
            foreach (var setter in doc.Descendants().Where(e => e.Name.LocalName == "Setter"
                         && (string?)e.Attribute("Property") == "FocusVisualStyle" && (string?)e.Attribute("Value") == "{x:Null}"))
            {
                var style = setter.Ancestors().First(a => a.Name.LocalName == "Style");
                if (!DrawsFocus(style)) offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)setter).LineNumber} style {(string?)style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))}");
            }

            // FocusVisualStyle="{x:Null}" on an element: its own template or style must draw focus
            foreach (var element in doc.Descendants().Where(e => (string?)e.Attribute("FocusVisualStyle") == "{x:Null}"))
            {
                if (!DrawsFocus(element)) offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} {element.Name.LocalName}");
            }
        }
        Assert.True(offenders.Count == 0, "Focus rectangle hidden with no focus state of its own:\n" + string.Join("\n", offenders));
    }
}
