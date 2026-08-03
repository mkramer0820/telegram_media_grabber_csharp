using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Application.Tests.Fakes;

/// <summary>In-memory <see cref="IStateRepository"/> fake mirroring the real SQLite semantics closely enough for orchestration tests.</summary>
public sealed class FakeStateRepository : IStateRepository
{
    private readonly Dictionary<long, int> _lastMessageIds = new();
    private readonly Dictionary<(long ChatId, int MessageId), (string FilePath, string? ContentHash)> _downloadedFiles = new();
    private readonly Dictionary<(string TargetChat, string DedupKey), string> _uploadedFiles = new();
    private readonly Dictionary<string, TelegramEntity> _resolvedEntities = new();

    public Task<int?> GetLastMessageIdAsync(long chatId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_lastMessageIds.TryGetValue(chatId, out var v) ? v : (int?)null);

    public Task SetLastMessageIdAsync(long chatId, int messageId, CancellationToken cancellationToken = default)
    {
        if (!_lastMessageIds.TryGetValue(chatId, out var current) || messageId > current)
        {
            _lastMessageIds[chatId] = messageId;
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsDownloadedAsync(long chatId, int messageId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_downloadedFiles.ContainsKey((chatId, messageId)));

    public Task RecordDownloadedFileAsync(long chatId, int messageId, string filePath, string? contentHash = null, CancellationToken cancellationToken = default)
    {
        _downloadedFiles.TryAdd((chatId, messageId), (filePath, contentHash));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> matches = _downloadedFiles.Values
            .Where(v => v.ContentHash == contentHash)
            .Select(v => v.FilePath)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<(int MessageId, string FilePath)>> ListDownloadedRecordsAsync(long chatId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(int, string)> records = _downloadedFiles
            .Where(kv => kv.Key.ChatId == chatId)
            .Select(kv => (kv.Key.MessageId, kv.Value.FilePath))
            .ToList();
        return Task.FromResult(records);
    }

    public Task<(long ChatId, int MessageId)?> FindDownloadedRecordByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        foreach (var kv in _downloadedFiles)
        {
            if (kv.Value.FilePath == filePath)
            {
                return Task.FromResult<(long, int)?>(kv.Key);
            }
        }

        return Task.FromResult<(long, int)?>(null);
    }

    public Task UpdateDownloadedFilePathAsync(long chatId, int messageId, string filePath, string? contentHash = null, CancellationToken cancellationToken = default)
    {
        _downloadedFiles[(chatId, messageId)] = (filePath, contentHash);
        return Task.CompletedTask;
    }

    public Task<bool> IsFileUploadedAsync(string targetChat, string dedupKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_uploadedFiles.ContainsKey((targetChat, dedupKey)));

    public Task MarkFileUploadedAsync(string targetChat, string dedupKey, string filePath, CancellationToken cancellationToken = default)
    {
        _uploadedFiles.TryAdd((targetChat, dedupKey), filePath);
        return Task.CompletedTask;
    }

    public Task CacheResolvedEntityAsync(string configuredValue, TelegramEntity entity, CancellationToken cancellationToken = default)
    {
        _resolvedEntities[configuredValue] = entity;
        return Task.CompletedTask;
    }

    public Task<TelegramEntity?> GetCachedResolvedEntityAsync(string configuredValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(_resolvedEntities.TryGetValue(configuredValue, out var entity) ? entity : null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
