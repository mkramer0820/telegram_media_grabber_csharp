using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>Pure naming helpers for audiobook files — no I/O beyond the collision check in <see cref="FilenameSanitizer.DedupSuffixedPath"/>.</summary>
public static class AudiobookNaming
{
    /// <summary>
    /// The book's destination directory: <c>destRoot/{author}/{novelTitle}</c>
    /// (sanitized). Shared by <see cref="BuildDestinationPath"/> and
    /// inference logic so both agree on exactly where a book's files live.
    /// </summary>
    public static string BookDir(string destRoot, AudiobookMetadata metadata)
    {
        var authorDir = FilenameSanitizer.Sanitize(metadata.Author, fallbackStem: "Unknown Author");
        var novelDir = FilenameSanitizer.Sanitize(metadata.NovelTitle, fallbackStem: "Unknown Title");
        return Path.Combine(destRoot, authorDir, novelDir);
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
        string destRoot, AudiobookMetadata metadata, ParseResult info, string extension)
    {
        var padded = info.Number.Padded;
        var baseName = info.Subtitle is { Length: > 0 } subtitle
            ? $"{metadata.NovelTitle} - {info.Number.Label} {padded} - {subtitle}{extension}"
            : $"{metadata.NovelTitle} - {info.Number.Label} {padded}{extension}";

        var filename = FilenameSanitizer.Sanitize(baseName);
        return Path.Combine(BookDir(destRoot, metadata), filename);
    }
}
