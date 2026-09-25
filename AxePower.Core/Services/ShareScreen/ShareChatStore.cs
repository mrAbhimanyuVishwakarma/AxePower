using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Persists in-room chat history across application restarts, updates, and reinstalls.
/// Stored as DPAPI-protected data in %LocalAppData%\AxePower\Chat\share-chat.json. The legacy
/// filename is retained so existing plaintext history can be migrated in place.
/// </summary>
public sealed class ShareChatStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = "AxePower.Friends.Chat.v1"u8.ToArray();
    private readonly string _chatDirectory;
    private readonly LogService? _log;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly object _writeLock = new();

    public const int MaxPersistedMessages = 500;

    public string ChatDirectory => _chatDirectory;
    public string ChatFilePath => Path.Combine(_chatDirectory, "share-chat.json");

    public ShareChatStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Chat"))
    {
    }

    public ShareChatStore(
        string chatDirectory,
        LogService? log = null,
        Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null)
    {
        _chatDirectory = chatDirectory;
        _log = log;
        _protect = protect ?? (bytes => ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// Loads saved messages from disk. Returns empty list if missing or corrupt.
    /// </summary>
    public List<ShareChatMessage> LoadMessages()
    {
        lock (_writeLock)
        {
            try
            {
                if (!File.Exists(ChatFilePath))
                {
                    return new List<ShareChatMessage>();
                }

                var stored = File.ReadAllBytes(ChatFilePath);
                // Migrate the earlier plain-JSON format without losing the owner's history.
                var json = LooksLikeJson(stored)
                    ? Encoding.UTF8.GetString(stored)
                    : Encoding.UTF8.GetString(_unprotect(stored));
                var messages = JsonSerializer.Deserialize<List<ShareChatMessage>>(json, JsonOptions);
                return messages ?? new List<ShareChatMessage>();
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[ShareChat] Could not load persisted chat messages: {ex.Message}");
                return new List<ShareChatMessage>();
            }
        }
    }

    /// <summary>
    /// Saves messages to disk atomically so updates and abrupt terminations never corrupt the file.
    /// </summary>
    public void SaveMessages(IEnumerable<ShareChatMessage> messages)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(_chatDirectory);

                var capped = messages.TakeLast(MaxPersistedMessages).ToList();
                var json = JsonSerializer.Serialize(capped, JsonOptions);
                var encrypted = _protect(Encoding.UTF8.GetBytes(json));

                var tempFile = ChatFilePath + ".temp";
                var backupFile = ChatFilePath + ".backup";

                File.WriteAllBytes(tempFile, encrypted);

                if (File.Exists(ChatFilePath))
                {
                    File.Replace(tempFile, ChatFilePath, backupFile, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempFile, ChatFilePath);
                }
            }
            catch (Exception ex)
            {
                _log?.LogError("[ShareChat] Could not save chat messages to disk", ex);
            }
        }
    }

    private static bool LooksLikeJson(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (char.IsWhiteSpace((char)value)) continue;
            return value is (byte)'[' or (byte)'{';
        }
        return false;
    }

    /// <summary>
    /// Appends a single message and persists the updated history.
    /// </summary>
    public void AppendMessage(ShareChatMessage message)
    {
        lock (_writeLock)
        {
            var list = LoadMessages();
            list.Add(message);
            SaveMessages(list);
        }
    }

    /// <summary>
    /// Clears persisted chat history.
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            try
            {
                if (File.Exists(ChatFilePath)) File.Delete(ChatFilePath);
                var backup = ChatFilePath + ".backup";
                if (File.Exists(backup)) File.Delete(backup);
                var temp = ChatFilePath + ".temp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[ShareChat] Could not clear chat history file: {ex.Message}");
            }
        }
    }
}
