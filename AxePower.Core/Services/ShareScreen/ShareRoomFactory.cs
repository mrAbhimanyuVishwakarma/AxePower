using System;
using System.Security.Cryptography;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Mints the identifiers for one sharing session.
///
/// The six-digit code exists so it can be read out over voice chat; it is deliberately not the
/// thing that protects the stream. The secret is <see cref="ShareRoom.PathName"/>, an 80-bit
/// random string that forms the viewer URL, so an unguessable link is what grants access.
/// </summary>
public static class ShareRoomFactory
{
    public static ShareRoom Create()
    {
        return new ShareRoom(
            Code: RandomDigits(6),
            PathName: "axe" + RandomHex(10),
            PublisherToken: RandomHex(16),
            ViewerToken: RandomHex(16));
    }

    private static string RandomDigits(int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }
        return new string(chars);
    }

    private static string RandomHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
}
