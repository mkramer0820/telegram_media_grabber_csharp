using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>Outcome of one <see cref="VerifyService.RunChannelAsync"/> call.</summary>
public sealed record VerifySummary(int Checked, int Corrected, int Errors)
{
    public static VerifySummary operator +(VerifySummary a, VerifySummary b) =>
        new(a.Checked + b.Checked, a.Corrected + b.Corrected, a.Errors + b.Errors);
}

/// <summary>
/// Re-verifies <c>audiobook_mode</c> episode numbers against Telegram
/// directly: <c>--mode verify</c>. Online (one batched get-messages
/// request per channel) — re-derives each already-tagged file's true
/// number from Telegram's raw document filename and corrects any
/// mismatch. See PROJECT_STATE.md §10 for why this exists: files tagged
/// before the bare-numeric filename support existed used the message ID
/// as a placeholder number, which this repairs.
/// </summary>
public sealed class VerifyService(ITelegramClient client, IStateRepository stateRepository, AudiobookProcessingService audiobookProcessor)
{
    private readonly IFilenameParser _parserChain = FilenameParserChain.Default;

    public async Task<VerifySummary> RunChannelAsync(
        ChannelOptions channel, string downloadRoot, string audiobooksDestDir,
        IProgress<string>? report = null, CancellationToken cancellationToken = default)
    {
        if (channel.Metadata is null)
        {
            return new VerifySummary(0, 0, 0);
        }

        var entity = await client.ResolveEntityAsync(channel.Id, cancellationToken);
        var records = await stateRepository.ListDownloadedRecordsAsync(entity.Id, cancellationToken);
        if (records.Count == 0)
        {
            return new VerifySummary(0, 0, 0);
        }

        var messageIds = records.Select(r => r.MessageId).ToList();
        var messages = await client.GetMessagesAsync(entity, messageIds, cancellationToken);

        var checkedCount = 0;
        var corrected = 0;
        var errors = 0;

        for (var i = 0; i < records.Count; i++)
        {
            var (messageId, filePath) = records[i];
            var message = messages[i];
            if (message is null || !File.Exists(filePath))
            {
                continue;
            }

            checkedCount++;
            try
            {
                var newPath = await VerifyOneAsync(channel, entity.Id, messageId, filePath, message.DeriveFilename(), downloadRoot, audiobooksDestDir, cancellationToken);
                if (newPath is not null)
                {
                    corrected++;
                    report?.Report($"Corrected {Path.GetFileName(filePath)} -> {Path.GetFileName(newPath)}");
                }
            }
            catch (Exception exc)
            {
                errors++;
                report?.Report($"Error {Path.GetFileName(filePath)}: {exc.Message}");
            }
        }

        return new VerifySummary(checkedCount, corrected, errors);
    }

    private async Task<string?> VerifyOneAsync(
        ChannelOptions channel, long chatId, int messageId, string filePath, string trueRawFilename,
        string downloadRoot, string audiobooksDestDir, CancellationToken cancellationToken)
    {
        var trueInfo = _parserChain.TryParse(trueRawFilename);
        if (trueInfo is null)
        {
            // Telegram's own filename carries no number either — nothing more trustworthy to correct to.
            return null;
        }

        var currentEpisode = AudiobookProcessingService.ParseTaggedEpisodeNumber(filePath);
        if (currentEpisode == trueInfo.Number.Value)
        {
            return null;
        }

        var effectiveDestDir = AudiobookNaming.EffectiveDestRoot(channel, downloadRoot, audiobooksDestDir);
        var newPath = audiobookProcessor.ApplyTagging(filePath, trueInfo, channel.Metadata!, effectiveDestDir, channel.MediaServerSubdir);
        var contentHash = ContentHash.OfFile(newPath);
        await stateRepository.UpdateDownloadedFilePathAsync(chatId, messageId, newPath, contentHash, cancellationToken);
        return newPath;
    }
}
