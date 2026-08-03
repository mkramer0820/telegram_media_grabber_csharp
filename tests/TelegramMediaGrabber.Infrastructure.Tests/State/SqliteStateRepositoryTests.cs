using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Infrastructure.State;

namespace TelegramMediaGrabber.Infrastructure.Tests.State;

/// <summary>
/// Exercises <see cref="SqliteStateRepository"/> against a real temp-file
/// SQLite database (never in-memory/mocked — AGENTS.md §6.2), covering the
/// documented contract of every <c>IStateRepository</c> member.
/// </summary>
public sealed class SqliteStateRepositoryTests : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly SqliteStateRepository _repository;

    /// <summary>Creates a fresh repository backed by a unique temp-file database per test.</summary>
    public SqliteStateRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"tmg-state-tests-{Guid.NewGuid():N}.db");
        _repository = new SqliteStateRepository(_databasePath);
    }

    /// <summary>Disposes the repository and deletes the temp database and its WAL/SHM siblings.</summary>
    public async ValueTask DisposeAsync()
    {
        await _repository.DisposeAsync();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task GetLastMessageId_DefaultsToNull_ThenRoundTrips()
    {
        Assert.Null(await _repository.GetLastMessageIdAsync(1));

        await _repository.SetLastMessageIdAsync(1, 42);

        Assert.Equal(42, await _repository.GetLastMessageIdAsync(1));
    }

    [Fact]
    public async Task SetLastMessageId_NeverRegressesBelowStoredValue()
    {
        await _repository.SetLastMessageIdAsync(1, 100);
        await _repository.SetLastMessageIdAsync(1, 50);

        Assert.Equal(100, await _repository.GetLastMessageIdAsync(1));

        await _repository.SetLastMessageIdAsync(1, 150);

        Assert.Equal(150, await _repository.GetLastMessageIdAsync(1));
    }

    [Fact]
    public async Task LastMessageId_IsTrackedIndependentlyPerChat()
    {
        await _repository.SetLastMessageIdAsync(1, 10);
        await _repository.SetLastMessageIdAsync(2, 999);

        Assert.Equal(10, await _repository.GetLastMessageIdAsync(1));
        Assert.Equal(999, await _repository.GetLastMessageIdAsync(2));
    }

    [Fact]
    public async Task IsDownloaded_IsFalseUntilRecorded()
    {
        Assert.False(await _repository.IsDownloadedAsync(1, 5));

        await _repository.RecordDownloadedFileAsync(1, 5, "C:/media/file.bin");

        Assert.True(await _repository.IsDownloadedAsync(1, 5));
    }

    [Fact]
    public async Task RecordDownloadedFile_IsIdempotent_SecondCallDoesNotClobberFirst()
    {
        await _repository.RecordDownloadedFileAsync(1, 5, "C:/media/original.bin", "hash-a");
        await _repository.RecordDownloadedFileAsync(1, 5, "C:/media/different.bin", "hash-b");

        var records = await _repository.ListDownloadedRecordsAsync(1);
        var record = Assert.Single(records);
        Assert.Equal(5, record.MessageId);
        Assert.Equal("C:/media/original.bin", record.FilePath);
    }

    [Fact]
    public async Task FindByContentHash_ReturnsAllMatches_OrEmptyWhenNone()
    {
        Assert.Empty(await _repository.FindByContentHashAsync("nope"));

        await _repository.RecordDownloadedFileAsync(1, 1, "C:/a.bin", "shared-hash");
        await _repository.RecordDownloadedFileAsync(1, 2, "C:/b.bin", "shared-hash");
        await _repository.RecordDownloadedFileAsync(1, 3, "C:/c.bin", "other-hash");

        var matches = await _repository.FindByContentHashAsync("shared-hash");

        Assert.Equal(2, matches.Count);
        Assert.Contains("C:/a.bin", matches);
        Assert.Contains("C:/b.bin", matches);
    }

    [Fact]
    public async Task ListDownloadedRecords_ReturnsAllRowsForOneChat_EmptyForUnknownChat()
    {
        await _repository.RecordDownloadedFileAsync(1, 1, "C:/a.bin");
        await _repository.RecordDownloadedFileAsync(1, 2, "C:/b.bin");
        await _repository.RecordDownloadedFileAsync(2, 1, "C:/other-chat.bin");

        var chat1Records = await _repository.ListDownloadedRecordsAsync(1);
        Assert.Equal(2, chat1Records.Count);

        var unknownChatRecords = await _repository.ListDownloadedRecordsAsync(999);
        Assert.Empty(unknownChatRecords);
    }

    [Fact]
    public async Task FindDownloadedRecordByPath_ReturnsNull_WhenNoMatch()
    {
        Assert.Null(await _repository.FindDownloadedRecordByPathAsync("C:/does/not/exist.bin"));

        await _repository.RecordDownloadedFileAsync(7, 9, "C:/media/known.bin");

        var found = await _repository.FindDownloadedRecordByPathAsync("C:/media/known.bin");

        Assert.NotNull(found);
        Assert.Equal(7, found!.Value.ChatId);
        Assert.Equal(9, found.Value.MessageId);
    }

    [Fact]
    public async Task UpdateDownloadedFilePath_CorrectsPathAndHash_OldPathNoLongerResolves()
    {
        await _repository.RecordDownloadedFileAsync(1, 1, "C:/old/path.bin", "old-hash");

        await _repository.UpdateDownloadedFilePathAsync(1, 1, "C:/new/path.bin", "new-hash");

        Assert.Null(await _repository.FindDownloadedRecordByPathAsync("C:/old/path.bin"));

        var found = await _repository.FindDownloadedRecordByPathAsync("C:/new/path.bin");
        Assert.NotNull(found);
        Assert.Equal(1, found!.Value.ChatId);
        Assert.Equal(1, found.Value.MessageId);

        var hashMatches = await _repository.FindByContentHashAsync("new-hash");
        Assert.Contains("C:/new/path.bin", hashMatches);
    }

    [Fact]
    public async Task UpdateDownloadedFilePath_IsNoOp_ForUnknownChatAndMessagePair()
    {
        // Must not throw for an unrecognized (chatId, messageId) pair.
        await _repository.UpdateDownloadedFilePathAsync(404, 404, "C:/nowhere.bin", "hash");

        Assert.Null(await _repository.FindDownloadedRecordByPathAsync("C:/nowhere.bin"));
    }

    [Fact]
    public async Task IsFileUploaded_And_MarkFileUploaded_RoundTrip_ScopedPerTargetChat()
    {
        Assert.False(await _repository.IsFileUploadedAsync("chatA", "dedup-1"));

        await _repository.MarkFileUploadedAsync("chatA", "dedup-1", "C:/upload.bin");

        Assert.True(await _repository.IsFileUploadedAsync("chatA", "dedup-1"));
        Assert.False(await _repository.IsFileUploadedAsync("chatB", "dedup-1"));
    }

    [Fact]
    public async Task MarkFileUploaded_IsIdempotent()
    {
        await _repository.MarkFileUploadedAsync("chatA", "dedup-1", "C:/first.bin");
        await _repository.MarkFileUploadedAsync("chatA", "dedup-1", "C:/second.bin");

        Assert.True(await _repository.IsFileUploadedAsync("chatA", "dedup-1"));
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerialized_AndLeaveConsistentState()
    {
        const int concurrency = 150;

        var recordTasks = Enumerable.Range(0, concurrency)
            .Select(i => _repository.RecordDownloadedFileAsync(1, i, $"C:/media/file-{i}.bin", $"hash-{i}"))
            .ToArray();

        var progressTasks = Enumerable.Range(0, concurrency)
            .Select(i => _repository.SetLastMessageIdAsync(2, i))
            .ToArray();

        var uploadTasks = Enumerable.Range(0, concurrency)
            .Select(i => _repository.MarkFileUploadedAsync("chatX", $"dedup-{i}", $"C:/upload-{i}.bin"))
            .ToArray();

        // No exceptions should surface from any of the concurrent producers —
        // this is what proves the single-writer discipline actually holds up
        // under contention rather than racing the one shared connection.
        await Task.WhenAll(recordTasks.Concat(progressTasks).Concat(uploadTasks));

        var records = await _repository.ListDownloadedRecordsAsync(1);
        Assert.Equal(concurrency, records.Count);
        for (var i = 0; i < concurrency; i++)
        {
            Assert.Contains(records, r => r.MessageId == i && r.FilePath == $"C:/media/file-{i}.bin");
        }

        // The highest messageId submitted must win, regardless of completion order.
        Assert.Equal(concurrency - 1, await _repository.GetLastMessageIdAsync(2));

        for (var i = 0; i < concurrency; i++)
        {
            Assert.True(await _repository.IsFileUploadedAsync("chatX", $"dedup-{i}"));
        }
    }

    [Fact]
    public async Task GetCachedResolvedEntity_DefaultsToNull_ThenRoundTrips()
    {
        Assert.Null(await _repository.GetCachedResolvedEntityAsync("some_audiobook_channel"));

        var entity = new TelegramEntity(123456789, "Example Novel Audiobook", "some_audiobook_channel", "channel");
        await _repository.CacheResolvedEntityAsync("some_audiobook_channel", entity);

        var cached = await _repository.GetCachedResolvedEntityAsync("some_audiobook_channel");
        Assert.NotNull(cached);
        Assert.Equal(entity, cached);
    }

    [Fact]
    public async Task CacheResolvedEntity_OverwritesPreviousResolutionForSameConfiguredValue()
    {
        await _repository.CacheResolvedEntityAsync(
            "some_audiobook_channel", new TelegramEntity(1, "Old Title", "some_audiobook_channel", "channel"));
        await _repository.CacheResolvedEntityAsync(
            "some_audiobook_channel", new TelegramEntity(1, "New Title", "some_audiobook_channel", "channel"));

        var cached = await _repository.GetCachedResolvedEntityAsync("some_audiobook_channel");
        Assert.Equal("New Title", cached?.DisplayName);
    }

    [Fact]
    public async Task CacheResolvedEntity_HandlesNullUsernameAndKind()
    {
        await _repository.CacheResolvedEntityAsync(
            "-1001234567890", new TelegramEntity(-1001234567890, "Private Channel"));

        var cached = await _repository.GetCachedResolvedEntityAsync("-1001234567890");
        Assert.NotNull(cached);
        Assert.Null(cached!.Username);
        Assert.Null(cached.Kind);
    }
}
