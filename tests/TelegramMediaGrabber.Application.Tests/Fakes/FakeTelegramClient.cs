using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake for <see cref="ITelegramClient"/>, per AGENTS.md §6.1
/// (fakes over mocking frameworks). Records calls for assertions.
/// </summary>
public sealed class FakeTelegramClient : ITelegramClient
{
    private readonly Dictionary<string, TelegramEntity> _entitiesById;
    private readonly Dictionary<(long ChatId, int MessageId), TelegramMessage> _messagesByKey;
    private readonly List<TelegramMessage> _messagesToIterate;

    public List<string> ResolvedIds { get; } = [];

    /// <summary>Messages <see cref="WatchNewMessagesAsync"/> yields, in order, before completing (or blocking forever if left empty and not cancelled).</summary>
    public List<TelegramMessage> MessagesToWatch { get; init; } = [];
    public List<(TelegramEntity Entity, IReadOnlyList<string> FilePaths)> MediaGroupUploads { get; } = [];
    public List<(TelegramEntity Entity, string FilePath)> DocumentUploads { get; } = [];
    public Exception? UploadMediaGroupException { get; set; }
    public Func<IReadOnlyList<string>, Exception?>? UploadMediaGroupExceptionFactory { get; set; }

    public FakeTelegramClient(
        Dictionary<string, TelegramEntity>? entitiesById = null,
        Dictionary<(long, int), TelegramMessage>? messagesByKey = null,
        List<TelegramMessage>? messagesToIterate = null)
    {
        _entitiesById = entitiesById ?? [];
        _messagesByKey = messagesByKey ?? [];
        _messagesToIterate = messagesToIterate ?? [];
    }

    public Task ConnectAndAuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TelegramEntity> ResolveEntityAsync(string chatIdOrUsername, CancellationToken cancellationToken = default)
    {
        ResolvedIds.Add(chatIdOrUsername);
        if (_entitiesById.TryGetValue(chatIdOrUsername, out var entity))
        {
            return Task.FromResult(entity);
        }

        // Default: derive a stable numeric-ish id from the string's hash so tests can be terse.
        var fallback = new TelegramEntity(chatIdOrUsername.GetHashCode(), chatIdOrUsername);
        return Task.FromResult(fallback);
    }

    public async IAsyncEnumerable<TelegramMessage> IterMessagesAsync(
        TelegramEntity entity, int minId = 0, int? limit = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var yielded = 0;
        foreach (var message in _messagesToIterate.Where(m => m.ChatId == entity.Id && m.Id > minId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit is { } max && yielded >= max)
            {
                yield break;
            }

            yield return message;
            yielded++;
            await Task.Yield();
        }
    }

    /// <summary>Yields <see cref="MessagesToWatch"/> in order, then blocks until cancelled — mirroring a real "watch" stream that only ends when the caller stops it.</summary>
    public async IAsyncEnumerable<TelegramMessage> WatchNewMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var message in MessagesToWatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public Task<IReadOnlyList<TelegramMessage?>> GetMessagesAsync(
        TelegramEntity entity, IReadOnlyList<int> messageIds, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TelegramMessage?> result = messageIds
            .Select(id => _messagesByKey.GetValueOrDefault((entity.Id, id)))
            .ToList();
        return Task.FromResult(result);
    }

    public Task DownloadMediaAsync(
        TelegramEntity entity, TelegramMessage message, string destinationPath,
        IProgress<(long BytesDone, long BytesTotal)>? progress = null, CancellationToken cancellationToken = default)
    {
        File.WriteAllBytes(destinationPath, "fake media bytes"u8.ToArray());
        progress?.Report((17, 17));
        return Task.CompletedTask;
    }

    public Task<TelegramMessage> UploadDocumentAsync(
        TelegramEntity entity, string filePath, string caption = "",
        IProgress<(long BytesDone, long BytesTotal)>? progress = null, CancellationToken cancellationToken = default)
    {
        DocumentUploads.Add((entity, filePath));
        progress?.Report((1, 1));
        return Task.FromResult(new TelegramMessage(1, entity.Id, DateTimeOffset.UtcNow, Path.GetFileName(filePath), false, false, false, true));
    }

    public Task<IReadOnlyList<TelegramMessage>> UploadMediaGroupAsync(
        TelegramEntity entity, IReadOnlyList<string> filePaths, string caption = "",
        IProgress<(long BytesDone, long BytesTotal)>? progress = null, CancellationToken cancellationToken = default)
    {
        var exception = UploadMediaGroupExceptionFactory?.Invoke(filePaths) ?? UploadMediaGroupException;
        if (exception is not null)
        {
            throw exception;
        }

        MediaGroupUploads.Add((entity, filePaths));
        progress?.Report((1, 1));
        IReadOnlyList<TelegramMessage> result = filePaths
            .Select((f, i) => new TelegramMessage(i, entity.Id, DateTimeOffset.UtcNow, Path.GetFileName(f), false, false, false, true))
            .ToList();
        return Task.FromResult(result);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
