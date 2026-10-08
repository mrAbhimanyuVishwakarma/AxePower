using System;
using AxePower.Core.Services.Outcomes;

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

    /// <summary>A message with optional details (Prompt 30). The banner offers Details and Copy.</summary>
    public event EventHandler<UserMessage>? OnShowMessage;

    /// <summary>
    /// Shows <paramref name="message"/>. A failure is always shown, even with notifications turned
    /// off: it answers something the user just did, and silence would read as success.
    /// </summary>
    public void Show(UserMessage message)
    {
        if (!message.IsFailure && _settingsService?.GetSettings().ShowNotifications == false) return;
        if (OnShowMessage != null) OnShowMessage.Invoke(this, message);
        else OnShowNotification?.Invoke(this, message.Text);
    }

    /// <summary>
    /// "Could not <paramref name="action"/>" with what to do about it, from the error. A
    /// cancellation shows as cancelled, not as an error.
    /// </summary>
    public void ShowFailure(string action, Exception error) => Show(UserMessages.Failed(action, error));
}
