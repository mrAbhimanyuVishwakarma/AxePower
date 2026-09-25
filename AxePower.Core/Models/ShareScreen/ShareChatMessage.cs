using System;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// A chat message exchanged in an active room.
/// </summary>
public sealed record ShareChatMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("isSelf")] bool IsSelf = false,
    [property: JsonPropertyName("kind")] string Kind = ShareChatKinds.Text,
    [property: JsonPropertyName("attachmentId")] string AttachmentId = "",
    [property: JsonPropertyName("attachmentName")] string AttachmentName = "",
    [property: JsonPropertyName("attachmentSize")] long AttachmentSize = 0,
    [property: JsonPropertyName("attachmentSha256")] string AttachmentSha256 = "",
    [property: JsonPropertyName("chunkIndex")] int ChunkIndex = 0,
    [property: JsonPropertyName("chunkCount")] int ChunkCount = 0,
    [property: JsonPropertyName("chunkData")] string ChunkData = "")
{
    [JsonIgnore]
    public string FormattedTime => Timestamp.ToString("t");

    [JsonIgnore]
    public string SenderInitial => !string.IsNullOrWhiteSpace(Sender) ? Sender[..1].ToUpperInvariant() : "?";

    [JsonIgnore]
    public bool IsText => string.Equals(Kind, ShareChatKinds.Text, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsAttachment => string.Equals(Kind, ShareChatKinds.File, StringComparison.Ordinal);

    [JsonIgnore]
    public string AttachmentSizeText => FormatBytes(AttachmentSize);

    [JsonIgnore]
    public string AttachmentStatusText => IsAttachment ? "Received - SHA-256 verified" : string.Empty;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / (1024d * 1024d):0.#} MB";
    }
}

/// <summary>Wire-level message kinds used by the room chat and attachment protocol.</summary>
public static class ShareChatKinds
{
    public const string Text = "text";
    public const string File = "file";
    public const string FileOffer = "file-offer";
    public const string FileChunk = "file-chunk";
    public const string FileComplete = "file-complete";
}
