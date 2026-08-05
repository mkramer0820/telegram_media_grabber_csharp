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

    /// <summary>
    /// Resolves one channel's whole batch of new messages in a single
    /// deterministic pass, processed in chronological/upload order --
    /// <paramref name="chronological"/> must already be oldest-first.
    /// </summary>
    /// <remarks>
    /// <see cref="Resolve"/> calls <see cref="AudiobookProcessingService.InferNextEpisodeNumber"/>
    /// fresh off disk every time it's invoked, which only gives the right
    /// answer if it always runs after every chronologically-earlier file
    /// has already been tagged into the destination folder. The normal
    /// download pipeline runs several files concurrently and only tags a
    /// file once its *download* finishes, not in upload order -- so two
    /// files with no parsable episode number (e.g. a track only labeled
    /// "Unknown Track", sitting between two properly-named episodes) can
    /// finish downloading close together and both read the same "next"
    /// number off disk before either has written its own tagged file,
    /// producing a collision. Resolving the whole batch up front,
    /// sequentially, in upload order, before any concurrent downloading
    /// starts, closes that race -- and, the actual point, gives untitled
    /// files the number their position implies: if "Ep 59 - Power of Ki"
    /// is the newest properly-named file and three untitled files were
    /// posted after it, they become Ep 60/61/62 in posting order, not
    /// whatever order their downloads happened to finish in.
    /// </remarks>
    public static IReadOnlyDictionary<int, ParseResult?> ResolveBatch(
        ChapterParsingService parsingService,
        IReadOnlyList<(int MessageId, string RawFilename)> chronological,
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
        var nextInferred = AudiobookProcessingService.InferNextEpisodeNumber(bookDir).Value;

        var results = new Dictionary<int, ParseResult?>();
        foreach (var (messageId, rawFilename) in chronological)
        {
            var result = parsingService.Resolve(rawFilename, overrides, () =>
            {
                var number = ChapterNumber.ForChapter(nextInferred);
                nextInferred++;
                return new ParseResult(number, Subtitle: null, MatchedBy: "InferNextChapter", ParseConfidence.Inferred);
            });

            // An explicitly-numbered chapter (parsed from the filename, or
            // an override) anchors where the *next* inferred number should
            // continue from, so a run of untitled files following a known
            // "Ep N" becomes N+1, N+2, ... instead of colliding with
            // whatever the disk-scan baseline happened to be. Volume
            // numbers are a separate space (AGENTS.md §2) and must never
            // influence chapter inference.
            if (result is { Confidence: not ParseConfidence.Inferred, Number.Kind: ContentUnitKind.Chapter })
            {
                nextInferred = Math.Max(nextInferred, result.Number.Value + 1);
            }

            results[messageId] = result;
        }

        return results;
    }
}
