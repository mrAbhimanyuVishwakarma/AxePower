using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.ViewModels.Music;

public partial class LyricLineViewModel(TimeSpan timestamp, string text) : ObservableObject
{
    public TimeSpan Timestamp { get; } = timestamp;
    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isActive;
}

