using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Imports and stores room attachments encrypted for the current Windows user.
/// Files are never executed or opened automatically; callers must explicitly export them.
/// </summary>
public sealed class ShareAttachmentStore
{
    public const long MaxAttachmentBytes = 8L * 1024L * 1024L;
    public const int WireChunkBytes = 24 * 1024;
    public const int MaxWireChunks = (int)((MaxAttachmentBytes + WireChunkBytes - 1) / WireChunkBytes);

    private static readonly byte[] Entropy = "AxePower.Friends.Attachments.v1"u8.ToArray();
    private readonly string _directory;
    private readonly LogService? _log;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public ShareAttachmentStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxePower", "Chat", "Attachments"))
    {
    }

    public ShareAttachmentStore(
        string directory,
        LogService? log = null,
        Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null)
    {
        _directory = directory;
        _log = log;
        _protect = protect ?? (bytes => ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    public PreparedAttachment Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The selected attachment no longer exists.", sourcePath);

        var info = new FileInfo(sourcePath);
        if (info.Length <= 0) throw new InvalidDataException("Empty files cannot be shared.");
        if (info.Length > MaxAttachmentBytes)
            throw new InvalidDataException("Attachments are limited to 8 MB in this release.");

        var bytes = File.ReadAllBytes(sourcePath);
        var id = Guid.NewGuid().ToString("N");
        var name = SanitizeFileName(info.Name);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        SaveEncrypted(id, bytes);
        return new PreparedAttachment(id, name, bytes.LongLength, sha256, bytes);
    }

    public void Accept(string id, string name, long declaredSize, string expectedSha256, byte[] bytes)
    {
        if (!IsValidId(id)) throw new InvalidDataException("Invalid attachment identifier.");
        if (bytes.LongLength != declaredSize) throw new InvalidDataException("Attachment size verification failed.");
        if (bytes.LongLength <= 0 || bytes.LongLength > MaxAttachmentBytes)
            throw new InvalidDataException("Attachment size is outside the allowed range.");

        _ = SanitizeFileName(name);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(expectedSha256.ToLowerInvariant())))
        {
            throw new InvalidDataException("Attachment integrity verification failed.");
        }

        SaveEncrypted(id, bytes);
    }

    public bool IsAvailable(string id) => IsValidId(id) && File.Exists(GetEncryptedPath(id));

    public void Export(string id, string destinationPath)
    {
        if (!IsValidId(id)) throw new InvalidDataException("Invalid attachment identifier.");
        var encrypted = File.ReadAllBytes(GetEncryptedPath(id));
        var bytes = _unprotect(encrypted);
        File.WriteAllBytes(destinationPath, bytes);
    }

    private void SaveEncrypted(string id, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var encrypted = _protect(bytes);
        var path = GetEncryptedPath(id);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, path, overwrite: true);
    }

    private string GetEncryptedPath(string id) => Path.Combine(_directory, id + ".axefile");

    private static bool IsValidId(string id) =>
        id.Length == 32 && id.All(c => char.IsAsciiHexDigit(c));

    public static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(leaf)) return "attachment.bin";
        foreach (var invalid in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(invalid, '_');
        return leaf.Length <= 120 ? leaf : leaf[..120];
    }
}

public sealed record PreparedAttachment(
    string Id,
    string Name,
    long Size,
    string Sha256,
    byte[] Bytes);
