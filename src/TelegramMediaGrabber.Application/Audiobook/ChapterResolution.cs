using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>
/// Ties <see cref="ChapterParsingService"/> and
/// <see cref="AudiobookProcessingService.InferNextEpisodeNumber"/>
/// together for the common case: resolve a chapter/volume number for one
/// file, given its owning channel's config (overrides + metadata).
/// Shared by the normal download path and the reprocess repair flow so
/// they can't drift out of sync on precedence rules.
/// </summary>
public static class ChapterResolution
{
    /// <returns>
    /// The resolved <see cref="ParseResult"/>, or null if an override
    /// says this file should be skipped entirely.
    /// </returns>
    public static ParseResult? Resolve(
        ChapterParsingService parsingService,
        string rawFilename,
        ChannelOptions channel,
        string audiobooksDestDir)
    {
        if (channel.Metadata is null)
        {
            throw new InvalidOperationException(
                $"Channel '{channel.Name}' has no audiobook metadata configured; cannot resolve a chapter number.");
        }

        var overrides = channel.Overrides.Count > 0 ? new ChannelOverrideLookup(channel.Overrides) : null;
        var bookDir = AudiobookNaming.BookDir(audiobooksDestDir, channel.Metadata, channel.MediaServerSubdir);

        return parsingService.Resolve(
            rawFilename,
            overrides,
            () => new ParseResult(
                AudiobookProcessingService.InferNextEpisodeNumber(bookDir),
                Subtitle: null,
                MatchedBy: "InferNextChapter",
                ParseConfidence.Inferred));
    }
}
