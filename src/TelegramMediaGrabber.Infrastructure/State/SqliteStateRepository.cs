using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Infrastructure.State;

/// <summary>
/// SQLite-backed implementation of <see cref="IStateRepository"/>, matching
/// PROJECT_STATE.md §4's three-table schema (<c>chat_progress</c>,
/// <c>downloaded_files</c>, <c>uploaded_files</c>) with <c>WAL</c> journal
/// mode.
/// </summary>
/// <remarks>
/// <para>
/// Single-writer discipline: this class owns exactly one
/// <see cref="SqliteConnection"/> for its entire lifetime and never touches
/// it from more than one logical operation at a time. Rather than a raw
/// <c>SemaphoreSlim(1,1)</c>, every public operation (reads included, since
/// a single ADO.NET connection is not safe for concurrent commands even for
/// reads) is packaged as a unit of work and pushed onto a
/// <see cref="System.Threading.Channels.Channel{T}"/>; a single background
/// task drains the channel and executes each unit against the connection,
/// one at a time, in submission order. This is the CSHARP_PORT_GUIDE.md §5
/// "preferred" option: it decouples "request an operation" from "the
/// operation actually running," which makes shutdown clean (complete the
/// channel and await the drain task, rather than hoping a semaphore isn't
/// held).
/// </para>
/// <para>
/// Construction performs synchronous local disk I/O (create the parent
/// directory, open the database file, run <c>CREATE TABLE IF NOT EXISTS</c>
/// and set <c>PRAGMA journal_mode=WAL</c>) — all fast, non-network calls —
/// so callers get a fully-initialized, ready-to-use repository without an
/// async factory step.
/// </para>
/// </remarks>
public sealed class SqliteStateRepository : IStateRepository
{
    private const string CreateChatProgressTableSql =
        """
        CREATE TABLE IF NOT EXISTS chat_progress (
            chat_id INTEGER PRIMARY KEY,
            last_message_id INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private const string CreateDownloadedFilesTableSql =
        """
        CREATE TABLE IF NOT EXISTS downloaded_files (
            chat_id INTEGER NOT NULL,
            message_id INTEGER NOT NULL,
            file_path TEXT NOT NULL,
            content_hash TEXT,
            downloaded_at TEXT NOT NULL,
            PRIMARY KEY (chat_id, message_id)
        );
        CREATE INDEX IF NOT EXISTS idx_downloaded_files_content_hash
            ON downloaded_files(content_hash);
        """;

    private const string CreateUploadedFilesTableSql =
        """
        CREATE TABLE IF NOT EXISTS uploaded_files (
            target_chat TEXT NOT NULL,
            dedup_key TEXT NOT NULL,
            file_path TEXT NOT NULL,
            uploaded_at TEXT NOT NULL,
            PRIMARY KEY (target_chat, dedup_key)
        );
        """;

    private const string CreateResolvedEntitiesTableSql =
        """
        CREATE TABLE IF NOT EXISTS resolved_entities (
            configured_value TEXT PRIMARY KEY,
            chat_id INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            username TEXT,
            kind TEXT,
            resolved_at TEXT NOT NULL
        );
        """;

    private readonly SqliteConnection _connection;
    private readonly Channel<Func<Task>> _writerQueue;
    private readonly Task _writerLoop;
    private bool _disposed;

    /// <summary>
    /// Opens (creating if necessary) the SQLite database at
    /// <paramref name="databasePath"/>, ensures the parent directory and
    /// schema exist, enables WAL mode, and starts the single-writer
    /// background loop.
    /// </summary>
    /// <param name="databasePath">
    /// Filesystem path to the SQLite database file, e.g.
    /// <c>data/state.db</c>. The parent directory is created if missing.
    /// </param>
    public SqliteStateRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = _connection.CreateCommand())
        {
            schema.CommandText = string.Join(
                Environment.NewLine,
                CreateChatProgressTableSql,
                CreateDownloadedFilesTableSql,
                CreateUploadedFilesTableSql,
                CreateResolvedEntitiesTableSql);
            schema.ExecuteNonQuery();
        }

        _writerQueue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _writerLoop = Task.Run(RunWriterLoopAsync);
    }

    /// <inheritdoc />
    public Task<int?> GetLastMessageIdAsync(long chatId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT last_message_id FROM chat_progress WHERE chat_id = $chatId;";
            command.Parameters.AddWithValue("$chatId", chatId);
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is null or DBNull ? (int?)null : Convert.ToInt32(result);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetLastMessageIdAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO chat_progress (chat_id, last_message_id, updated_at)
                VALUES ($chatId, $messageId, $updatedAt)
                ON CONFLICT(chat_id) DO UPDATE SET
                    last_message_id = excluded.last_message_id,
                    updated_at = excluded.updated_at
                WHERE excluded.last_message_id > chat_progress.last_message_id;
                """;
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> IsDownloadedAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM downloaded_files WHERE chat_id = $chatId AND message_id = $messageId LIMIT 1;";
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$messageId", messageId);
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is not null;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task RecordDownloadedFileAsync(
        long chatId, int messageId, string filePath, string? contentHash = null, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO downloaded_files (chat_id, message_id, file_path, content_hash, downloaded_at)
                VALUES ($chatId, $messageId, $filePath, $contentHash, $downloadedAt)
                ON CONFLICT(chat_id, message_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$filePath", filePath);
            command.Parameters.AddWithValue("$contentHash", (object?)contentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$downloadedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindByContentHashAsync(
        string contentHash, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT file_path FROM downloaded_files WHERE content_hash = $contentHash;";
            command.Parameters.AddWithValue("$contentHash", contentHash);
            var results = new List<string>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)results;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(int MessageId, string FilePath)>> ListDownloadedRecordsAsync(
        long chatId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT message_id, file_path FROM downloaded_files WHERE chat_id = $chatId ORDER BY message_id;";
            command.Parameters.AddWithValue("$chatId", chatId);
            var results = new List<(int MessageId, string FilePath)>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            return (IReadOnlyList<(int MessageId, string FilePath)>)results;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<(long ChatId, int MessageId)?> FindDownloadedRecordByPathAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT chat_id, message_id FROM downloaded_files WHERE file_path = $filePath LIMIT 1;";
            command.Parameters.AddWithValue("$filePath", filePath);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return ((long ChatId, int MessageId)?)null;
            }

            return (reader.GetInt64(0), reader.GetInt32(1));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateDownloadedFilePathAsync(
        long chatId, int messageId, string filePath, string? contentHash = null, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE downloaded_files
                SET file_path = $filePath, content_hash = $contentHash
                WHERE chat_id = $chatId AND message_id = $messageId;
                """;
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$filePath", filePath);
            command.Parameters.AddWithValue("$contentHash", (object?)contentHash ?? DBNull.Value);
            // Intentionally a no-op (0 rows affected) when the (chatId, messageId)
            // pair doesn't already exist — this is state repair, not upsert.
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> IsFileUploadedAsync(
        string targetChat, string dedupKey, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM uploaded_files WHERE target_chat = $targetChat AND dedup_key = $dedupKey LIMIT 1;";
            command.Parameters.AddWithValue("$targetChat", targetChat);
            command.Parameters.AddWithValue("$dedupKey", dedupKey);
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is not null;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkFileUploadedAsync(
        string targetChat, string dedupKey, string filePath, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO uploaded_files (target_chat, dedup_key, file_path, uploaded_at)
                VALUES ($targetChat, $dedupKey, $filePath, $uploadedAt)
                ON CONFLICT(target_chat, dedup_key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$targetChat", targetChat);
            command.Parameters.AddWithValue("$dedupKey", dedupKey);
            command.Parameters.AddWithValue("$filePath", filePath);
            command.Parameters.AddWithValue("$uploadedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task CacheResolvedEntityAsync(
        string configuredValue, TelegramEntity entity, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO resolved_entities (configured_value, chat_id, display_name, username, kind, resolved_at)
                VALUES ($configuredValue, $chatId, $displayName, $username, $kind, $resolvedAt)
                ON CONFLICT(configured_value) DO UPDATE SET
                    chat_id = excluded.chat_id,
                    display_name = excluded.display_name,
                    username = excluded.username,
                    kind = excluded.kind,
                    resolved_at = excluded.resolved_at;
                """;
            command.Parameters.AddWithValue("$configuredValue", configuredValue);
            command.Parameters.AddWithValue("$chatId", entity.Id);
            command.Parameters.AddWithValue("$displayName", entity.DisplayName);
            command.Parameters.AddWithValue("$username", (object?)entity.Username ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", (object?)entity.Kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$resolvedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TelegramEntity?> GetCachedResolvedEntityAsync(
        string configuredValue, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(async ct =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT chat_id, display_name, username, kind FROM resolved_entities WHERE configured_value = $configuredValue;";
            command.Parameters.AddWithValue("$configuredValue", configuredValue);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return (TelegramEntity?)null;
            }

            return new TelegramEntity(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }, cancellationToken);
    }

    /// <summary>
    /// Packages <paramref name="work"/> as a single unit and hands it to the
    /// background writer loop, awaiting its result. This is how every public
    /// method — reads and writes alike — is serialized through the one
    /// connection this repository owns.
    /// </summary>
    private async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Bind the caller's cancellation token to the completion source so an
        // awaiting caller observes cancellation promptly even if the queue is
        // backed up; the queued delegate below still runs (and its own
        // ExecuteXAsync calls will throw OperationCanceledException, which we
        // swallow into a no-op completion since the caller already moved on).
        await using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(),
            completion);

        await _writerQueue.Writer.WriteAsync(
            async () =>
            {
                try
                {
                    var result = await work(cancellationToken).ConfigureAwait(false);
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            cancellationToken).ConfigureAwait(false);

        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Drains the writer queue, executing one unit of work at a time.</summary>
    private async Task RunWriterLoopAsync()
    {
        await foreach (var work in _writerQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await work().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _writerQueue.Writer.Complete();
        await _writerLoop.ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
