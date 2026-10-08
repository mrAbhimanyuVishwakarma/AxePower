using System;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>Reading the room links friends paste or receive in invites.</summary>
public static class ShareRoomLink
{
    public enum JoinAddressProblem { None, Empty, Invalid }

    /// <summary>
    /// Turns what the user typed into a room address: trimmed, <c>http://</c> added when no scheme
    /// is given, and only http or https accepted.
    /// </summary>
    public static JoinAddressProblem TryNormalize(string? input, out string address, out Uri? uri)
    {
        address = string.Empty;
        uri = null;
        var raw = input?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return JoinAddressProblem.Empty;

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return JoinAddressProblem.Invalid;
        }

        address = raw;
        uri = parsed;
        return JoinAddressProblem.None;
    }

    /// <summary>The viewer token from the link's <c>#v=</c> fragment, or empty when the link has none.</summary>
    public static string ParseViewerSecret(Uri uri)
    {
        var fragment = uri.Fragment.TrimStart('#');
        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Equals("v", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return string.Empty;
    }
}
