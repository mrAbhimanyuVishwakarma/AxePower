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
    public const string Control = "Control";
    public const string Connection = "Connection";
    public const string Maintenance = "Maintenance";
    public const string Utilities = "Utilities";
    public const string AccountAndSettings = "Account and settings";

    public static IReadOnlyList<Entry> Entries { get; } = new[]
    {
        new Entry("Eco", "Eco", PowerModes),
        new Entry("Smart", "Smart", PowerModes),
        new Entry("Beast", "Beast", PowerModes),
        new Entry("GameMode", "Game Mode", PowerModes),
        new Entry("Dashboard", "Dashboard", Control),
        new Entry("GameModePage", "Game Mode", Control),
        new Entry("Battery", "Battery", Control),
        new Entry("Music", "Music", Control),
        new Entry("PhoneControl", "Phone Control", Connection),
        new Entry("SecondMonitor", "Second Monitor", Connection),
        new Entry("LanDrop", "LAN Drop", Connection),
        new Entry("PhoneLink", "Phone Link", Connection),
        new Entry("UpdatePc", "Windows Update", Maintenance),
        new Entry("SystemCare", "System Care", Maintenance),
        new Entry("Uninstaller", "Uninstaller", Maintenance),
        new Entry("Debloat", "Debloat", Maintenance),
        new Entry("Terminal", "Terminal", Utilities),
        new Entry("Tools", "Tools", Utilities),
        new Entry("Trust", "Trust & Permissions", AccountAndSettings),
        new Entry("Settings", "Settings", AccountAndSettings, Permanent: true),
        new Entry("Profile", "Profile", AccountAndSettings),
        new Entry("Exit", "Exit AxePower", AccountAndSettings, Permanent: true),
    };

    /// <summary>Menu element name for an entry, e.g. "TrayItem_Eco".</summary>
    public static string ElementName(string id) => "TrayItem_" + id;
}
