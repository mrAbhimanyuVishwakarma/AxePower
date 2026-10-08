using System;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Where Share Screen's embedded browser may go (FINAL_SECURITY_REVIEW M2). The page comes from
/// the room's host, so it may navigate within that host's origin (the room page and its stream)
/// and to about:blank, which AxePower uses to stop playback. Anything else, such as a lookalike
/// sign-in page, is refused, and pop-ups are blocked by the view.
/// </summary>
public static class WebViewNavigationPolicy
{
    /// <summary>True when <paramref name="target"/> may load in a view opened at <paramref name="opened"/>.</summary>
    public static bool IsAllowed(string? target, string? opened)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (string.Equals(target, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var to)) return false;
        if (string.IsNullOrWhiteSpace(opened) || !Uri.TryCreate(opened, UriKind.Absolute, out var from)) return false;
        if (to.Scheme is not ("http" or "https")) return false;
        return string.Equals(to.Scheme, from.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(to.Host, from.Host, StringComparison.OrdinalIgnoreCase)
            && to.Port == from.Port;
    }
}
