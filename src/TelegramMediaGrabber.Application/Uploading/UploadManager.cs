using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Application.Progress;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Application.Uploading;

/// <summary>
/// Scans configured upload jobs and uploads their files, batched into
/// per-chat Telegram media groups. Depends only on interfaces — see
/// CSHARP_PORT_GUIDE.md §8.
/// </summary>
public sealed class UploadManager
{
    /// <summary>Telegram's hard limit on files per media group (album) message — not a tunable.</summary>
    public const int MediaGroupMaxSize = 10;

    // Explicit pause between media-group batches, proactively spacing out
    // requests rather than only reacting to FloodWait after the fact.
    private static readonly TimeSpan InterBatchDelay = TimeSpan.FromSeconds(3);

    private readonly ITelegramClient _client;
    private readonly Application.State.IStateRepository _stateRepository;
    private readonly IUploadProgressReporter _reporter;
    private readonly Dictionary<string, TelegramEntity> _entityCache = new();

    public UploadManager(ITelegramClient client, Application.State.IStateRepository stateRepository, IUploadProgressReporter? reporter = null)
    {
        _client = client;
        _stateRepository = stateRepository;
        _reporter = reporter ?? NullUploadProgressReporter.Instance;
    }

    /// <summary>
    /// Scans every job's source directory (non-recursively, or fully
    /// recursively per <see cref="UploadJobOptions.Recursive"/>) and
    /// returns the files to upload, in job-declaration order.
    /// </summary>
    public IReadOnlyList<(string FilePath, string TargetChat)> BuildQueue(IReadOnlyList<UploadJobOptions> jobs)
    {
        var queue = new List<(string, string)>();
        foreach (var job in jobs)
        {
            if (!Directory.Exists(job.SourceDir))
            {
                continue;
            }

            var searchOption = job.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(job.SourceDir, "*", searchOption)
                .OrderBy(f => f, StringComparer.Ordinal);
            queue.AddRange(files.Select(f => (f, job.TargetChat)));
        }

        return queue;
    }

    /// <summary>
    /// Uploads every queued file, batched by contiguous target chat and
    /// chunked to <see cref="MediaGroupMaxSize"/>. Files already recorded
    /// as uploaded (by dedup key, scoped per target chat) are skipped.
    /// </summary>
    public async Task ProcessQueueAsync(
        IReadOnlyList<(string FilePath, string TargetChat)> queue, CancellationToken cancellationToken = default)
    {
        var total = queue.Count;
        var uploaded = 0;
        var skipped = 0;

        var pending = new List<(string FilePath, string TargetChat, string DedupKey)>();
        foreach (var (filePath, targetChat) in queue)
        {
            var dedupKey = ContentHash.ComputeUploadDedupKey(filePath);
            if (await _stateRepository.IsFileUploadedAsync(targetChat, dedupKey, cancellationToken))
            {
                skipped++;
                _reporter.OnFileSkipped(Path.GetFileName(filePath));
                _reporter.OnQueueProgress(new UploadQueueProgress(total, uploaded, skipped, Done: false));
            }
            else
            {
                pending.Add((filePath, targetChat, dedupKey));
            }
        }

        var batches = BuildBatches(pending);

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var targetChat = batch[0].TargetChat;
            var filePaths = batch.Select(b => b.FilePath).ToList();

            try
            {
                var entity = await ResolveCachedAsync(targetChat, cancellationToken);
                var joinedNames = string.Join(", ", filePaths.Select(Path.GetFileName));
                var progress = new Progress<(long BytesDone, long BytesTotal)>(p =>
                    _reporter.OnFileProgress(new UploadFileProgress(joinedNames, p.BytesDone, p.BytesTotal)));

                await _client.UploadMediaGroupAsync(entity, filePaths, progress: progress, cancellationToken: cancellationToken);
            }
            catch (Exception exc)
            {
                foreach (var item in batch)
                {
                    _reporter.OnFileError(Path.GetFileName(item.FilePath), exc.Message);
                }

                _reporter.OnQueueProgress(new UploadQueueProgress(total, uploaded, skipped, Done: false));
                continue;
            }

            foreach (var item in batch)
            {
                await _stateRepository.MarkFileUploadedAsync(item.TargetChat, item.DedupKey, item.FilePath, cancellationToken);
                uploaded++;
                _reporter.OnFileComplete(Path.GetFileName(item.FilePath));
            }

            _reporter.OnQueueProgress(new UploadQueueProgress(total, uploaded, skipped, Done: false));

            if (batchIndex < batches.Count - 1)
            {
                await Task.Delay(InterBatchDelay, cancellationToken);
            }
        }

        _reporter.OnQueueProgress(new UploadQueueProgress(total, uploaded, skipped, Done: true));
    }

    /// <summary>
    /// Groups contiguous same-target-chat runs (the queue is already
    /// job-contiguous from <see cref="BuildQueue"/>), then chunks each run
    /// to <see cref="MediaGroupMaxSize"/> — a batch never spans two target
    /// chats, since a media group is a single message to a single chat.
    /// </summary>
    private static List<List<(string FilePath, string TargetChat, string DedupKey)>> BuildBatches(
        List<(string FilePath, string TargetChat, string DedupKey)> pending)
    {
        var batches = new List<List<(string, string, string)>>();
        var i = 0;
        while (i < pending.Count)
        {
            var chat = pending[i].TargetChat;
            var j = i;
            while (j < pending.Count && pending[j].TargetChat == chat)
            {
                j++;
            }

            var group = pending.GetRange(i, j - i);
            for (var start = 0; start < group.Count; start += MediaGroupMaxSize)
            {
                batches.Add(group.GetRange(start, Math.Min(MediaGroupMaxSize, group.Count - start)));
            }

            i = j;
        }

        return batches;
    }

    private async Task<TelegramEntity> ResolveCachedAsync(string chatIdOrUsername, CancellationToken cancellationToken)
    {
        if (_entityCache.TryGetValue(chatIdOrUsername, out var cached))
        {
            return cached;
        }

        var entity = await _client.ResolveEntityAsync(chatIdOrUsername, cancellationToken);
        _entityCache[chatIdOrUsername] = entity;
        return entity;
    }
}
