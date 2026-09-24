using System.Diagnostics;
using System.Windows;
using AxePower.App.Windows;
using AxePower.Core.Services;

namespace AxePower.App.Services;

/// <summary>
/// The questions the Game Mode settings ask. The app shows <see cref="CountdownConfirmWindow"/>;
/// tests use a fake, so no automated test waits on a dialog or restarts the PC.
/// </summary>
public interface IGameModePrompts
{
    /// <summary>10-second caution before games are moved to the dedicated GPU in Game Mode.</summary>
    bool ConfirmDedicatedGpu();

    /// <summary>10-second caution before the display mode is changed in firmware.</summary>
    bool ConfirmDisplayMode(GpuDisplayMode mode);

    /// <summary>A game is running in Game Mode; true to leave Game Mode anyway. Stays by default.</summary>
    bool ConfirmLeaveGameMode(string game);

    /// <summary>True for "Restart now".</summary>
    bool AskRestartNow(GpuDisplayMode mode);

    void RestartWindows();
}

public sealed class WindowGameModePrompts : IGameModePrompts
{
    public const int CautionSeconds = 10;

    public bool ConfirmDedicatedGpu() => Show(
        "Use the dedicated GPU in Game Mode?",
        "While Game Mode is on, your games are set to Windows' \"High performance\" GPU. This uses more power " +
        "and heat. Games already running keep their GPU until restarted. Everything goes back when Game Mode ends.",
        "Turn on", "Cancel", CautionSeconds);

    public bool ConfirmDisplayMode(GpuDisplayMode mode) => Show(
        mode == GpuDisplayMode.DedicatedOnly ? "Switch to dedicated GPU only?" : "Switch back to Optimus?",
        mode == GpuDisplayMode.DedicatedOnly
            ? "The screen will be driven by the dedicated GPU alone, like \"NVIDIA GPU only\" in NVIDIA Control Panel. " +
              "Best for games, but battery life drops a lot. Takes effect after a restart."
            : "The integrated GPU drives the screen again and Windows picks a GPU per app. Better battery life. " +
              "Takes effect after a restart.",
        "Switch", "Cancel", CautionSeconds);

    public bool ConfirmLeaveGameMode(string game) => Show(
        "A game is running",
        $"{game} is open, so Game Mode is holding maximum performance for it. Leave Game Mode anyway?",
        "Switch anyway", "Stay in Game Mode", CautionSeconds);

    public bool AskRestartNow(GpuDisplayMode mode) => Show(
        "Restart to apply",
        $"The display mode is set to {(mode == GpuDisplayMode.DedicatedOnly ? "Dedicated GPU only" : "Optimus")}. " +
        "Save your work before restarting.",
        "Restart now", "Later", 0);

    public void RestartWindows() =>
        Process.Start(new ProcessStartInfo("shutdown", "/r /t 5 /c \"AxePower: restarting to apply the GPU display mode\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });

    private static bool Show(string title, string message, string confirm, string cancel, int seconds)
    {
        var window = new CountdownConfirmWindow(title, message, confirm, cancel, seconds);
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && owner != window) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }
}
