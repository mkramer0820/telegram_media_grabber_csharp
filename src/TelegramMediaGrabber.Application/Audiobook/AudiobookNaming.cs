using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>Pure naming helpers for audiobook files — no I/O beyond the collision check in <see cref="FilenameSanitizer.DedupSuffixedPath"/>.</summary>
public static class AudiobookNaming
{
    /// <summary>
    /// The destination root to actually use for one channel: its own
    /// <c>{downloadRoot}/Audiobooks</c> if <see cref="ChannelOptions.LocalOnly"/>
    /// is set, otherwise the configured <c>LOCAL_MEDIA_SERVER</c> value.
    /// Centralized here so the download/reprocess/verify paths can't drift
    /// out of sync on which channels stay local.
    /// </summary>
    public static string EffectiveDestRoot(ChannelOptions channel, string downloadRoot, string configuredDestDir) =>
        channel.LocalOnly ? Path.Combine(downloadRoot, "Audiobooks") : configuredDestDir;

    /// <summary>
    /// The book's destination directory: <c>destRoot/{novelTitle}</c>
    /// (sanitized), or <c>destRoot/{subdirOverride}</c> if one is given —
    /// deliberately no author-level folder (most people browse audiobooks
    /// by title, not author; author stays in ID3/MP4 tags only, via
    /// <see cref="AudiobookMetadata.Author"/>). Shared by
    /// <see cref="BuildDestinationPath"/> and inference logic so both
    /// agree on exactly where a book's files live.
    /// </summary>
    public static string BookDir(string destRoot, AudiobookMetadata metadata, string? subdirOverride = null)
    {
        var dirName = FilenameSanitizer.Sanitize(
            string.IsNullOrWhiteSpace(subdirOverride) ? metadata.NovelTitle : subdirOverride,
            fallbackStem: "Unknown Title");
        return Path.Combine(destRoot, dirName);
    }

    /// <summary>
    /// Builds the Title tag value: <c>"{label} {n} - {subtitle}"</c>, or
    /// <c>"{novelTitle} - {label} {n}"</c> when there's no subtitle.
    /// </summary>
    public static string FormatTitle(string novelTitle, ParseResult info) =>
        info.Subtitle is { Length: > 0 } subtitle
            ? $"{info.Number.Label} {info.Number.Value} - {subtitle}"
            : $"{novelTitle} - {info.Number.Label} {info.Number.Value}";

    /// <summary>
    /// Builds the sanitized, collision-free-candidate destination path.
    /// Final collision handling (dedup suffixing) is the caller's
    /// responsibility via <see cref="FilenameSanitizer.DedupSuffixedPath"/>.
    /// </summary>
    public static string BuildDestinationPath(
        string destRoot, AudiobookMetadata metadata, ParseResult info, string extension, string? subdirOverride = null)
    {
        var padded = info.Number.Padded;
        var baseName = info.Subtitle is { Length: > 0 } subtitle
            ? $"{metadata.NovelTitle} - {info.Number.Label} {padded} - {subtitle}{extension}"
            : $"{metadata.NovelTitle} - {info.Number.Label} {padded}{extension}";

        var filename = FilenameSanitizer.Sanitize(baseName);
        return Path.Combine(BookDir(destRoot, metadata, subdirOverride), filename);
    }
}
