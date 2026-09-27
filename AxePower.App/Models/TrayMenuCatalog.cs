using System.Collections.Generic;

namespace AxePower.App.Models;

/// <summary>
/// The items in AxePower's tray (taskbar) menu, in menu order. Settings → Taskbar preferences
/// lets each non-permanent item be hidden; MainWindow applies that each time the menu opens.
/// </summary>
public static class TrayMenuCatalog
{
    public sealed record Entry(string Id, string Title, string Group, bool Permanent = false);

    public const string PowerModes = "Power modes";
    public const string Pages = "Pages";
    public const string Tools = "Tools";
    public const string AlwaysShown = "Always shown";

    public static IReadOnlyList<Entry> Entries { get; } = new[]
    {
        new Entry("Eco", "Eco", PowerModes),
        new Entry("Smart", "Smart", PowerModes),
        new Entry("Beast", "Beast", PowerModes),
        new Entry("GameMode", "Game Mode", PowerModes),
        new Entry("Dashboard", "Dashboard", Pages),
        new Entry("Battery", "Battery", Pages),
        new Entry("PhoneControl", "Phone Control", Pages),
        new Entry("SecondMonitor", "Second Monitor", Pages),
        new Entry("Maintenance", "Maintenance", Tools),
        new Entry("Utilities", "Utilities", Tools),
        new Entry("UpdatePc", "Update PC", Tools),
        new Entry("Settings", "Settings", AlwaysShown, Permanent: true),
        new Entry("Exit", "Exit AxePower", AlwaysShown, Permanent: true),
    };

    /// <summary>Menu element name for an entry, e.g. "TrayItem_Eco".</summary>
    public static string ElementName(string id) => "TrayItem_" + id;
}
