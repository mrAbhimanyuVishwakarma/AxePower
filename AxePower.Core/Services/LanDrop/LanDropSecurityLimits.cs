using System;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Security, resource, and sizing limits for the LanDrop HTTP server.
/// Guards against memory exhaustion, disk exhaustion, slow clients, and unbounded concurrent connections.
/// </summary>
public sealed class LanDropSecurityLimits
{
    /// <summary>
    /// Maximum allowed size for HTTP request headers in bytes (default: 16 KB).
    /// Requests with headers exceeding this limit are rejected with 431 Request Header Fields Too Large.
    /// </summary>
    public int MaxHeaderSizeBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Maximum allowed length of a quick text / clipboard message in bytes (default: 64 KB).
    /// Text requests exceeding this limit are rejected with 413 Payload Too Large.
    /// </summary>
    public int MaxTextMessageSizeBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Maximum allowed size for an individual uploaded file in bytes (default: 2 GB).
    /// Files exceeding this limit are rejected with 413 Payload Too Large.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum allowed header size for a single multipart section in bytes (default: 8 KB).
    /// Multipart sections with headers exceeding this are rejected with 400 Bad Request.
    /// </summary>
    public int MaxMultipartPartHeaderSizeBytes { get; set; } = 8 * 1024;

    /// <summary>
    /// Maximum allowed total request body size across all parts in bytes (default: 2 GB + 1 MB).
    /// Requests declaring or streaming bodies exceeding this are rejected with 413 Payload Too Large.
    /// </summary>
    public long MaxTotalRequestSizeBytes { get; set; } = (2L * 1024 * 1024 * 1024) + (1024 * 1024);

    /// <summary>
    /// Maximum number of received file metadata items retained in memory session history (default: 100).
    /// </summary>
    public int MaxSessionFilesCount { get; set; } = 100;

    /// <summary>
    /// Maximum number of clipboard text messages retained in memory session history (default: 50).
    /// </summary>
    public int MaxClipboardMessagesCount { get; set; } = 50;

    /// <summary>
    /// Maximum total storage allowed across all files in the download directory in bytes (default: 10 GB).
    /// </summary>
    public long MaxSessionStorageBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent active TCP connections processed simultaneously (default: 10).
    /// Connections beyond this limit receive 503 Service Unavailable or are closed.
    /// </summary>
    public int MaxConcurrentClients { get; set; } = 10;

    /// <summary>
    /// Read timeout for socket operations before aborting an idle or slow client (default: 30 seconds).
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Write timeout for socket response operations (default: 30 seconds).
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum free disk space required on the destination volume before accepting an upload (default: 100 MB).
    /// Uploads with insufficient remaining space are rejected with 507 Insufficient Storage.
    /// </summary>
    public long MinFreeDiskSpaceBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// Maximum allowed failed authentication attempts from a single IP before temporary lockout (default: 5).
    /// </summary>
    public int MaxFailedAuthAttempts { get; set; } = 5;

    /// <summary>
    /// Duration of the rolling window for tracking failed authentication attempts (default: 1 minute).
    /// </summary>
    public TimeSpan FailedAuthWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Duration of lockout when a client exceeds allowed failed authentication attempts (default: 1 minute).
    /// </summary>
    public TimeSpan FailedAuthLockoutDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum lifetime of an active server session token before automatic expiration (default: 4 hours).
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// How long a client may go without a request before it is dropped from the connected list
    /// and anything it still has in flight is cancelled (default: 30 minutes).
    /// </summary>
    public TimeSpan ClientIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often a running server re-checks token expiry, idle clients, the bound address and the
    /// network's Windows profile (default: 30 seconds).
    /// </summary>
    public TimeSpan SessionWatchdogInterval { get; set; } = TimeSpan.FromSeconds(30);
}
