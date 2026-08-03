using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.Progress;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Downloading;

/// <summary>
/// Coordinates bounded-concurrency downloads for a set of channels.
/// Depends only on interfaces (<see cref="ITelegramClient"/>,
/// <see cref="IStateRepository"/>) so it's fully testable with fakes —
/// see CSHARP_PORT_GUIDE.md §8.
/// </summary>
public sealed class DownloadManager
{
    // Mirrors PROJECT_STATE.md §5: transient (non-FloodWait) errors get a
    // capped exponential backoff, distinct from FloodWait handling (which
    // lives inside ITelegramClient implementations).
    private const int MaxTransientRetries = 5;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    // Anti-ban pacing: a randomized human-plausible gap between downloads
    // per worker slot, mirroring the Python predecessor.
    private static readonly TimeSpan MinInterDownloadDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxInterDownloadDelay = TimeSpan.FromSeconds(5);

    private readonly ITelegramClient _client;
    private readonly IStateRepository _stateRepository;
    private readonly AudiobookProcessingService _audiobookProcessor;
    private readonly ChapterParsingService _parsingService;
    private readonly string _downloadRoot;
    private readonly string _audiobooksDestDir;
    private readonly SemaphoreSlim _semaphore;
    private readonly IDownloadProgressReporter _reporter;
    private readonly Random _random = new();

    /// <param name="audiobooksDestDir">
    /// Destination root for <c>audiobook_mode</c> channels after tagging
    /// (the configured <c>LOCAL_MEDIA_SERVER</c> — e.g. a Plex/Jellyfin
    /// library mount). Defaults to <c>{downloadRoot}/Audiobooks</c> only
    /// if not given; callers that have a real configured value (every Cli
    /// command except tests) MUST pass it explicitly, or tagged files
    /// silently land under the default instead of the user's actual media
    /// server — this is exactly the bug that made every real download
    /// land in the wrong place regardless of what LOCAL_MEDIA_SERVER was
    /// set to, since nothing wired it through to here before.
    /// </param>
    public DownloadManager(
        ITelegramClient client,
        IStateRepository stateRepository,
        AudiobookProcessingService audiobookProcessor,
        ChapterParsingService parsingService,
        string downloadRoot,
        int maxConcurrentDownloads,
        IDownloadProgressReporter? reporter = null,
        string? audiobooksDestDir = null)
    {
        _client = client;
        _stateRepository = stateRepository;
        _audiobookProcessor = audiobookProcessor;
        _parsingService = parsingService;
        _downloadRoot = downloadRoot;
        _audiobooksDestDir = audiobooksDestDir ?? Path.Combine(downloadRoot, "Audiobooks");
        _semaphore = new SemaphoreSlim(maxConcurrentDownloads);
        _reporter = reporter ?? NullDownloadProgressReporter.Instance;
    }

    /// <summary>Processes every channel concurrently, respecting the shared semaphore.</summary>
    public async Task RunAsync(IReadOnlyList<ChannelOptions> channels, CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(channels.Select(channel => ProcessChannelAsync(channel, cancellationToken)));
    }

    /// <summary>
    /// Resolves every configured channel once, then processes new messages
    /// as Telegram pushes them (<see cref="ITelegramClient.WatchNewMessagesAsync"/>)
    /// — no repeated history scans. Only ever downloads (the same
    /// per-file pipeline <see cref="RunAsync"/> uses, including
    /// <c>AudiobookMode</c> tagging and <c>AutoUploadTarget</c> if a
    /// channel has one set); it does not scan channel backlog itself. Run
    /// <see cref="RunAsync"/> manually first (or after any gap, e.g. this
    /// process having been down) to catch up on anything posted while not
    /// watching — that catch-up scan is a separate, deliberate step, not
    /// automatic here. Only returns when <paramref name="cancellationToken"/>
    /// is cancelled.
    /// </summary>
    /// <remarks>
    /// Captures each channel's current high-water mark (<see cref="IStateRepository.GetLastMessageIdAsync"/>)
    /// once at start and skips any pushed message at or below it, in
    /// addition to the normal <see cref="IStateRepository.IsDownloadedAsync"/>
    /// dedup check — Telegram's live update stream can redeliver a backlog
    /// of "new message" events on (re)connect that overlap with what a
    /// just-completed catch-up scan already covered, and relying on
    /// dedup alone was observed producing duplicate " (1)" files for
    /// exactly that reason when a watch immediately followed a catch-up
    /// in the same run.
    /// </remarks>
    public async Task WatchAsync(IReadOnlyList<ChannelOptions> channels, CancellationToken cancellationToken = default)
    {
        var byChatId = new Dictionary<long, (ChannelOptions Channel, TelegramEntity Entity, string OutputDir, int Watermark)>();
        foreach (var channel in channels)
        {
            var entity = await _client.ResolveEntityAsync(channel.Id, cancellationToken);
            var outputDir = Path.Combine(_downloadRoot, channel.OutputSubdir);
            Directory.CreateDirectory(outputDir);
            var watermark = await _stateRepository.GetLastMessageIdAsync(entity.Id, cancellationToken) ?? 0;
            byChatId[entity.Id] = (channel, entity, outputDir, watermark);
        }

        await foreach (var message in _client.WatchNewMessagesAsync(cancellationToken))
        {
            if (!byChatId.TryGetValue(message.ChatId, out var target))
            {
                continue;
            }

            var (channel, entity, outputDir, watermark) = target;

            if (message.Id <= watermark
                || !MatchesMediaTypes(message, channel.MediaTypes)
                || !EpisodeRangeExtractor.WantsEpisode(channel.EpisodeRange, message.DeriveFilename())
                || await _stateRepository.IsDownloadedAsync(entity.Id, message.Id, cancellationToken))
            {
                continue;
            }

            await DownloadOneAsync(channel, entity.Id, entity, message, outputDir, cancellationToken);
            await _stateRepository.SetLastMessageIdAsync(entity.Id, message.Id, cancellationToken);
        }
    }

    private async Task ProcessChannelAsync(ChannelOptions channel, CancellationToken cancellationToken)
    {
        var entity = await _client.ResolveEntityAsync(channel.Id, cancellationToken);
        var chatId = entity.Id;
        var lastMessageId = await _stateRepository.GetLastMessageIdAsync(chatId, cancellationToken) ?? 0;
        var minDate = channel.MinDate is { } date
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var outputDir = Path.Combine(_downloadRoot, channel.OutputSubdir);
        Directory.CreateDirectory(outputDir);

        var scanned = 0;
        var downloaded = 0;
        var highestSeen = lastMessageId;
        var tasks = new List<Task>();

        // Telegram's default iteration order is newest-message-first, so
        // message.Date is monotonically non-increasing as we iterate:
        // once we hit one message older than minDate, every subsequent
        // message is guaranteed older too — safe to stop scanning.
        // channel.MaxMessages (null means unbounded) caps how many of the
        // most recent messages are even fetched, for a channel with a
        // large backlog the user doesn't want scanned in full.
        await foreach (var message in _client.IterMessagesAsync(entity, lastMessageId, channel.MaxMessages, cancellationToken))
        {
            if (minDate is { } cutoff && message.Date < cutoff)
            {
                break;
            }

            scanned++;
            highestSeen = Math.Max(highestSeen, message.Id);

            if (!MatchesMediaTypes(message, channel.MediaTypes))
            {
                continue;
            }

            // Episode-range filtering never breaks the scan early (unlike
            // minDate): episode numbers embedded in filenames aren't
            // guaranteed to be monotonic with message order.
            if (!EpisodeRangeExtractor.WantsEpisode(channel.EpisodeRange, message.DeriveFilename()))
            {
                continue;
            }

            if (await _stateRepository.IsDownloadedAsync(chatId, message.Id, cancellationToken))
            {
                continue;
            }

            tasks.Add(DownloadOneAsync(channel, chatId, entity, message, outputDir, cancellationToken));
            downloaded++;

            _reporter.OnChannelProgress(new ChannelProgress(channel.Name, scanned, downloaded, Done: false));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        if (highestSeen > lastMessageId)
        {
            await _stateRepository.SetLastMessageIdAsync(chatId, highestSeen, cancellationToken);
        }

        _reporter.OnChannelProgress(new ChannelProgress(channel.Name, scanned, downloaded, Done: true));
    }

    private static bool MatchesMediaTypes(TelegramMessage message, IReadOnlyList<MediaType> wanted)
    {
        var effectiveWanted = wanted.Count > 0
            ? wanted
            : [MediaType.Photo, MediaType.Video, MediaType.Document];

        foreach (var type in effectiveWanted)
        {
            var matches = type switch
            {
                MediaType.Photo => message.HasPhoto,
                MediaType.Video => message.HasVideo,
                MediaType.Document => message.HasDocument,
                MediaType.Audio => message.HasAudio,
                _ => false,
            };

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private async Task DownloadOneAsync(
        ChannelOptions channel, long chatId, TelegramEntity entity, TelegramMessage message, string outputDir,
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var rawName = message.DeriveFilename();
            var safeName = FilenameSanitizer.Sanitize(rawName);
            var finalPath = FilenameSanitizer.DedupSuffixedPath(Path.Combine(outputDir, safeName));
            var tmpPath = finalPath + ".tmp";

            try
            {
                await DownloadWithRetriesAsync(channel.Name, entity, message, tmpPath, cancellationToken);
                // Atomic rename: only now is the file considered "downloaded" (AGENTS.md §3.1/§3.5).
                File.Move(tmpPath, finalPath);
            }
            catch (OperationCanceledException)
            {
                // Leave the .tmp file in place for resume — never rename a partial file.
                throw;
            }
            catch (Exception exc)
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }

                _reporter.OnFileError(channel.Name, message.Id, exc.Message);
                return;
            }

            var resultPath = finalPath;
            if (channel.AudiobookMode && channel.Metadata is not null)
            {
                try
                {
                    var info = ChapterResolution.Resolve(_parsingService, rawName, channel, _audiobooksDestDir);
                    if (info is not null)
                    {
                        resultPath = _audiobookProcessor.ApplyTagging(
                            finalPath, info, channel.Metadata, _audiobooksDestDir);
                    }
                }
                catch (Exception)
                {
                    // Don't lose an already-downloaded file if post-processing fails.
                }
            }

            var contentHash = await Task.Run(() => ContentHash.OfFile(resultPath), cancellationToken);
            await _stateRepository.RecordDownloadedFileAsync(chatId, message.Id, resultPath, contentHash, cancellationToken);
            _reporter.OnFileComplete(channel.Name, message.Id, resultPath);

            if (channel.AutoUploadTarget is { } target)
            {
                await AutoUploadAsync(target, resultPath, cancellationToken);
            }

            // Anti-ban pacing: space out consecutive downloads on this
            // worker slot with a randomized, human-plausible delay, held
            // for as long as the semaphore slot itself (matches the
            // Python predecessor). Skipped on cancellation so shutdown
            // isn't held up.
            try
            {
                await Task.Delay(RandomDelay(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down — don't hold the slot open for a pacing delay.
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Uploads a just-downloaded file to <c>channel.AutoUploadTarget</c>.
    /// Dedup-checked/recorded the same way <c>UploadManager</c> does
    /// (shared uploaded-files state, scoped by target chat), so re-running
    /// download mode never re-sends a file that already made it out.
    /// Failures are swallowed after logging via the reporter — an
    /// auto-upload problem must never be mistaken for the download itself
    /// having failed; the file is already durably on disk and marked
    /// downloaded.
    /// </summary>
    private async Task AutoUploadAsync(string targetChat, string filePath, CancellationToken cancellationToken)
    {
        var dedupKey = ContentHash.ComputeUploadDedupKey(filePath);
        if (await _stateRepository.IsFileUploadedAsync(targetChat, dedupKey, cancellationToken))
        {
            return;
        }

        try
        {
            var entity = await _client.ResolveEntityAsync(targetChat, cancellationToken);
            await _client.UploadDocumentAsync(entity, filePath, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exc)
        {
            _reporter.OnFileError(targetChat, 0, $"auto-upload failed: {exc.Message}");
            return;
        }

        await _stateRepository.MarkFileUploadedAsync(targetChat, dedupKey, filePath, cancellationToken);
    }


    private async Task DownloadWithRetriesAsync(
        string chatName, TelegramEntity entity, TelegramMessage message, string tmpPath, CancellationToken cancellationToken)
    {
        var transientAttempt = 0;
        while (true)
        {
            try
            {
                var progress = new Progress<(long BytesDone, long BytesTotal)>(p =>
                    _reporter.OnFileProgress(new FileProgress(chatName, message.Id, Path.GetFileName(tmpPath), p.BytesDone, p.BytesTotal)));

                await _client.DownloadMediaAsync(entity, message, tmpPath, progress, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                transientAttempt++;
                if (transientAttempt >= MaxTransientRetries)
                {
                    throw;
                }

                var backoff = TimeSpan.FromSeconds(
                    Math.Min(BaseBackoff.TotalSeconds * Math.Pow(2, transientAttempt - 1), MaxBackoff.TotalSeconds)
                    + _random.NextDouble());
                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    private TimeSpan RandomDelay()
    {
        var range = MaxInterDownloadDelay - MinInterDownloadDelay;
        return MinInterDownloadDelay + range * _random.NextDouble();
    }
}
