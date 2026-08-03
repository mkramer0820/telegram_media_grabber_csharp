using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Files;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>Outcome of one <see cref="ReprocessService.RunAsync"/> call.</summary>
public sealed record ReprocessSummary(int Processed, int ProcessedWithoutRecord, int Errors);

/// <summary>
/// Repairs <c>audiobook_mode</c> files stuck in staging: <c>--mode reprocess</c>.
/// Fully offline — never touches Telegram. Two cases handled, per
/// AGENTS.md §7.1/CSHARP_PORT_GUIDE.md: a file recorded in state but never
/// tagged/relocated (post-processing didn't run at the time), and a file
/// with NO state record at all (predates state tracking) — both get
/// tagged/relocated; only the latter skips the state-repair step.
/// </summary>
public sealed class ReprocessService(IStateRepository stateRepository, AudiobookProcessingService audiobookProcessor, ChapterParsingService parsingService)
{
    /// <summary>Lists files still sitting in a channel's staging directory — anything there needs reprocessing, by definition.</summary>
    public static IReadOnlyList<string> FindStuckFiles(string downloadRoot, ChannelOptions channel)
    {
        var stagingDir = Path.Combine(downloadRoot, channel.OutputSubdir);
        if (!Directory.Exists(stagingDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(stagingDir).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reprocesses every stuck file across the given audiobook_mode channels.</summary>
    public async Task<ReprocessSummary> RunAsync(
        IReadOnlyList<ChannelOptions> channels,
        string downloadRoot,
        string audiobooksDestDir,
        IProgress<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        var processed = 0;
        var processedWithoutRecord = 0;
        var errors = 0;

        foreach (var channel in channels.Where(c => c.AudiobookMode && c.Metadata is not null))
        {
            foreach (var filePath in FindStuckFiles(downloadRoot, channel))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (newPath, hadRecord) = await ReprocessOneAsync(channel, filePath, audiobooksDestDir, cancellationToken);
                    if (hadRecord)
                    {
                        processed++;
                        report?.Report($"Reprocessed {Path.GetFileName(filePath)} -> {Path.GetFileName(newPath)}");
                    }
                    else
                    {
                        processedWithoutRecord++;
                        report?.Report(
                            $"Reprocessed {Path.GetFileName(filePath)} -> {Path.GetFileName(newPath)} (no downloaded_files record — state not updated)");
                    }
                }
                catch (Exception exc)
                {
                    errors++;
                    report?.Report($"Error {Path.GetFileName(filePath)}: {exc.Message}");
                }
            }
        }

        return new ReprocessSummary(processed, processedWithoutRecord, errors);
    }

    private async Task<(string NewPath, bool HadRecord)> ReprocessOneAsync(
        ChannelOptions channel, string filePath, string audiobooksDestDir, CancellationToken cancellationToken)
    {
        var record = await stateRepository.FindDownloadedRecordByPathAsync(filePath, cancellationToken);

        var info = ChapterResolution.Resolve(parsingService, Path.GetFileName(filePath), channel, audiobooksDestDir)
            ?? throw new InvalidOperationException($"Override for '{filePath}' skips this file; it should not have been scanned.");

        var newPath = audiobookProcessor.ApplyTagging(filePath, info, channel.Metadata!, audiobooksDestDir);

        if (record is null)
        {
            return (newPath, false);
        }

        var (chatId, messageId) = record.Value;
        var contentHash = ContentHash.OfFile(newPath);
        await stateRepository.UpdateDownloadedFilePathAsync(chatId, messageId, newPath, contentHash, cancellationToken);
        return (newPath, true);
    }
}
