using System;

namespace AxePower.Core.Services;

public class NotificationService
{
    private readonly SettingsService? _settingsService;

    /// <summary>
    /// Keeps small unit-test and tooling setups working without a settings store. The application
    /// uses the settings-aware constructor below.
    /// </summary>
    public NotificationService()
    {
    }

    public NotificationService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event EventHandler<string>? OnShowNotification;

    public void Show(string message)
    {
        // This is the single path used by in-app desktop banners. Keeping the master switch here
        // means every feature honours it instead of each caller having to remember its own check.
        if (_settingsService?.GetSettings().ShowNotifications == false) return;

        OnShowNotification?.Invoke(this, message);
    }

    public void ShowNotification(string message) => Show(message);
}
