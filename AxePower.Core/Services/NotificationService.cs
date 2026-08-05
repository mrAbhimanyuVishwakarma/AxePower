using System;

namespace AxePower.Core.Services;

public class NotificationService
{
    public event EventHandler<string>? OnShowNotification;

    public void Show(string message)
    {
        OnShowNotification?.Invoke(this, message);
    }
}
