using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Application.State;

/// <summary>
/// Persistent state store — SQLite-backed in Infrastructure
/// (<c>SqliteStateRepository</c>), matching PROJECT_STATE.md §4's three
/// tables. All writes MUST be serialized through a single writer (a
/// <c>Channel&lt;T&gt;</c>-based queue or <c>SemaphoreSlim(1,1)</c> — see
/// CSHARP_PORT_GUIDE.md §5) — never multiple connections/threads writing
/// concurrently.
/// </summary>
public interface IStateRepository : IAsyncDisposable
{
    // -- chat_progress ------------------------------------------------

    Task<int?> GetLastMessageIdAsync(long chatId, CancellationToken cancellationToken = default);

    /// <summary>Upserts, never regressing below the currently-stored value.</summary>
    Task SetLastMessageIdAsync(long chatId, int messageId, CancellationToken cancellationToken = default);

    // -- downloaded_files ------------------------------------------------

    Task<bool> IsDownloadedAsync(long chatId, int messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a completed download. Callers MUST only invoke this after
    /// the file has been atomically renamed into its final location
    /// (AGENTS.md §3) — never before, never for a .tmp path.
    /// </summary>
    Task RecordDownloadedFileAsync(
        long chatId, int messageId, string filePath, string? contentHash = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>All recorded (messageId, filePath) pairs for one chat — used by the reprocess/verify repair flows.</summary>
    Task<IReadOnlyList<(int MessageId, string FilePath)>> ListDownloadedRecordsAsync(
        long chatId, CancellationToken cancellationToken = default);

    /// <summary>Finds which (chatId, messageId) a file on disk belongs to, by its exact recorded path.</summary>
    Task<(long ChatId, int MessageId)?> FindDownloadedRecordByPathAsync(
        string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// State-repair only — corrects an existing row's file_path/content_hash
    /// after a post-processing step finishes late (reprocess/verify). Not a
    /// general-purpose overwrite; the row must already exist.
    /// </summary>
    Task UpdateDownloadedFilePathAsync(
        long chatId, int messageId, string filePath, string? contentHash = null, CancellationToken cancellationToken = default);

    // -- uploaded_files ------------------------------------------------

    Task<bool> IsFileUploadedAsync(string targetChat, string dedupKey, CancellationToken cancellationToken = default);

    Task MarkFileUploadedAsync(
        string targetChat, string dedupKey, string filePath, CancellationToken cancellationToken = default);

    // -- resolved_entities ------------------------------------------------

    /// <summary>
    /// Records whatever Telegram told us about a resolved chat/channel
    /// (permanent numeric ID, title, username, kind) against the config
    /// value that resolved to it (a username, t.me link, or invite link) —
    /// durable, "hunt for it later" reference info, independent of any
    /// download/upload activity. Overwrites any previous record for the
    /// same <paramref name="configuredValue"/> (a channel can rename
    /// itself; the latest resolution wins).
    /// </summary>
    Task CacheResolvedEntityAsync(
        string configuredValue, TelegramEntity entity, CancellationToken cancellationToken = default);

    /// <summary>The most recently cached resolution for <paramref name="configuredValue"/>, or null if it's never been resolved/cached.</summary>
    Task<TelegramEntity?> GetCachedResolvedEntityAsync(
        string configuredValue, CancellationToken cancellationToken = default);
}
