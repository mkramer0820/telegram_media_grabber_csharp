using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Tries an ordered list of <see cref="IFilenameParser"/>s and returns the
/// first match. Order is an explicit, documented, tested property of the
/// list — see <see cref="Default"/> for the standard ordering (most
/// specific pattern first).
/// </summary>
public sealed class FilenameParserChain : IFilenameParser
{
    private readonly IReadOnlyList<IFilenameParser> _parsers;

    public FilenameParserChain(IEnumerable<IFilenameParser> parsers)
    {
        _parsers = parsers.ToList();
    }

    /// <summary>
    /// The standard chapter/volume parser ordering: "Ep n" pattern, then
    /// "Vol n" pattern, then the generic bare-number-anywhere fallback.
    /// Does not include override lookup — that's a separate concern
    /// resolved by <see cref="ChapterParsingService"/> before this chain
    /// is ever consulted.
    /// </summary>
    public static FilenameParserChain Default { get; } = new(
    [
        new ChapterPatternParser(),
        new VolumePatternParser(),
        new BareNumberParser(),
    ]);

    public ParseResult? TryParse(string rawFilename)
    {
        foreach (var parser in _parsers)
        {
            var result = parser.TryParse(rawFilename);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
