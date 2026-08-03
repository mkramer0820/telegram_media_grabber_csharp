namespace TelegramMediaGrabber.Application.Telegram;

/// <summary>
/// Abstraction over the MTProto client library (WTelegramClient in
/// Infrastructure — see CSHARP_PORT_GUIDE.md §6). No caller outside
/// Infrastructure should reference the underlying library's types
/// directly.
/// </summary>
/// <remarks>
/// Implementations MUST implement the FloodWait retry shape from
/// PROJECT_STATE.md §5 internally for every method that talks to
/// Telegram: sleep for exactly the server-requested duration plus a
/// small fixed buffer, capped retries, never a growing/exponential
/// multiple, never a tight loop.
/// </remarks>
public interface ITelegramClient : IAsyncDisposable
{
    /// <summary>Connects and completes authentication, reusing an existing session if present.</summary>
    Task ConnectAndAuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves a chat ID, "@username", or invite link to a concrete entity. Never auto-joins a channel.</summary>
    Task<TelegramEntity> ResolveEntityAsync(string chatIdOrUsername, CancellationToken cancellationToken = default);

    /// <summary>
    /// Iterates a chat's messages newest-first, starting after <paramref name="minId"/>
    /// (exclusive) — mirrors Telethon's <c>iter_messages(min_id=..., limit=...)</c>
    /// semantics. <paramref name="limit"/>, if set, caps the total number of
    /// messages yielded regardless of <paramref name="minId"/>.
    /// </summary>
    IAsyncEnumerable<TelegramMessage> IterMessagesAsync(
        TelegramEntity entity, int minId = 0, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Batched fetch of specific message IDs (one request). A null element means that message no longer exists.</summary>
    Task<IReadOnlyList<TelegramMessage?>> GetMessagesAsync(
        TelegramEntity entity, IReadOnlyList<int> messageIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams new messages as Telegram pushes them over the existing
    /// connection (MTProto Updates) — no polling. Yields messages from
    /// every chat this account receives updates for; the caller filters to
    /// the chats it cares about. Only completes when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<TelegramMessage> WatchNewMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads one message's media to <paramref name="destinationPath"/> (caller is responsible for atomic .tmp handling — see AGENTS.md §3).</summary>
    Task DownloadMediaAsync(
        TelegramEntity entity,
        TelegramMessage message,
        string destinationPath,
        IProgress<(long BytesDone, long BytesTotal)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads a single file as a document.</summary>
    Task<TelegramMessage> UploadDocumentAsync(
        TelegramEntity entity,
        string filePath,
        string caption = "",
        IProgress<(long BytesDone, long BytesTotal)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads up to 10 files as a single media group (album) — see PROJECT_STATE.md §5.</summary>
    Task<IReadOnlyList<TelegramMessage>> UploadMediaGroupAsync(
        TelegramEntity entity,
        IReadOnlyList<string> filePaths,
        string caption = "",
        IProgress<(long BytesDone, long BytesTotal)>? progress = null,
        CancellationToken cancellationToken = default);
}
