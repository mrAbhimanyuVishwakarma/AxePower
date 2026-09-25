using System;
using System.Collections.Generic;
using System.Linq;

namespace AxePower.Core.Services;

/// <summary>
/// Which background apps and services the Game Booster warns about before closing them. They can
/// still be closed; the user is told why it is risky and asked to confirm. Processes that must
/// never be closed are filtered out earlier by <see cref="AppKillerService.IsCriticalProcess"/>.
/// </summary>
public static class CloseCaution
{
    public const string SafeGroup = "Safe to close";
    public const string CautionGroup = "Caution";

    /// <summary>Prefixes of driver and hardware helper processes: audio, input, RGB, OEM and GPU tools.</summary>
    private static readonly (string Prefix, string What)[] HardwareHelpers =
    {
        ("RtkAud", "Realtek audio"), ("RAVBg", "Realtek audio"), ("RAVCpl", "Realtek audio"), ("Realtek", "Realtek audio"),
        ("Dolby", "Dolby audio"), ("Nahimic", "Nahimic audio"), ("A-Volute", "Nahimic audio"),
        ("Waves", "Waves audio"), ("MaxxAudio", "Waves audio"),
        ("Bluetooth", "Bluetooth"), ("BTServices", "Bluetooth"), ("ibtsiva", "Intel Bluetooth"),
        ("SynTP", "Synaptics touchpad"), ("ETDCtrl", "ELAN touchpad"), ("ETDService", "ELAN touchpad"),
        ("Lenovo", "Lenovo hardware"), ("LegionZone", "Lenovo Legion Zone"), ("ImController", "Lenovo hardware"),
        ("FnHotkey", "keyboard hotkeys"), ("LNBITSSvc", "Lenovo hardware"),
        ("NVIDIA", "NVIDIA graphics"), ("nvsphelper", "NVIDIA graphics"), ("NVDisplay", "NVIDIA graphics"),
        ("RadeonSoftware", "AMD graphics"), ("AMDRS", "AMD graphics"),
        ("IntelGraphics", "Intel graphics"), ("igfx", "Intel graphics"), ("IAStor", "Intel storage"),
        ("iCUE", "Corsair devices"), ("Corsair", "Corsair devices"), ("Razer", "Razer devices"),
        ("lghub", "Logitech devices"), ("LGHUB", "Logitech devices"), ("SteelSeries", "SteelSeries devices"),
        ("ArmouryCrate", "ASUS hardware"), ("ASUS", "ASUS hardware"), ("MSI.CentralServer", "MSI hardware"),
        ("Dragon Center", "MSI hardware"), ("OmenCommandCenter", "HP Omen hardware"), ("AlienFX", "Alienware lighting"),
    };

    /// <summary>Services whose stop has a lasting cost, with the reason shown to the user.</summary>
    public static readonly IReadOnlyDictionary<string, string> Services = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["WaaSMedicSvc"] = "Keeps Windows Update working. Stopping it can leave updates broken and the PC unpatched.",
    };

    /// <summary>
    /// Why closing this app is risky, or null when it is safe. <paramref name="hasOpenWindow"/> is
    /// true when the process shows a titled window, which may hold unsaved work.
    /// </summary>
    public static string? ForApp(string processName, bool hasOpenWindow)
    {
        var helper = HardwareHelpers.FirstOrDefault(h => processName.StartsWith(h.Prefix, StringComparison.OrdinalIgnoreCase));
        if (helper.Prefix != null)
            return $"Drives {helper.What}. Closing it can stop that hardware or its controls working until you sign in again.";

        return hasOpenWindow
            ? "Has an open window. Anything unsaved in it would be lost."
            : null;
    }

    public static string? ForService(string serviceName) => Services.TryGetValue(serviceName, out var reason) ? reason : null;
}
