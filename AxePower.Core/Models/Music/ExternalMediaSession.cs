using System;

namespace AxePower.Core.Models.Music;

/// <summary>
/// Another app's media session (a browser tab, VLC, etc.) as reported by Windows.
/// The flags describe what that app says it allows; commands may still be refused.
/// </summary>
public sealed record ExternalMediaSession(
    string SourceAppUserModelId,
    string AppName,
    string? Title,
    string? Artist,
    string? Album,
    byte[]? Thumbnail,
    TimeSpan Position,
    TimeSpan Duration,
    bool Playing,
    bool CanNext,
    bool CanPrevious,
    bool CanSeek,
    DateTime LastChangedUtc);
