using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Attempts to extract a chapter/volume number and subtitle from a raw
/// filename. Implementations are small and independently testable;
/// adding support for a new real-world filename shape means adding a new
/// implementation to <see cref="FilenameParserChain"/>, never editing an
/// existing one. See AGENTS.md §2.
/// </summary>
public interface IFilenameParser
{
    /// <param name="rawFilename">
    /// The original filename, with or without extension, exactly as it
    /// arrived from the source (e.g. Telegram's document filename).
    /// </param>
    /// <returns>A <see cref="ParseResult"/> if this parser matched, otherwise null.</returns>
    ParseResult? TryParse(string rawFilename);
}
