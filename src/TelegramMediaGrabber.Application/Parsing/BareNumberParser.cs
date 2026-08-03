using System.Text.RegularExpressions;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Finds a cleanly-delimited bare number or number range anywhere in the
/// filename stem — leading, trailing, or the whole stem — e.g. "1114",
/// "5-6", "Example Novel 1751-1846" (trailing range with a title prefix),
/// or "0001_0100_Another_Novel" (leading range with a title suffix,
/// "_" as the separator). A range uses its start number.
/// "Cleanly-delimited" means bounded by the stem's edges or a
/// whitespace/underscore/hyphen/dot separator on each side — a digit run
/// merely adjacent to other text without such a boundary does not count
/// (e.g. "randomname123text" has no match). Tried last in the chain —
/// it is deliberately the least specific pattern.
/// </summary>
public sealed partial class BareNumberParser : IFilenameParser
{
    [GeneratedRegex(@"(?:^|[\s_-])(?<start>\d+)(?:[-_](?<end>\d+))?(?=$|[\s_.-])")]
    private static partial Regex NumberTokenPattern();

    public ParseResult? TryParse(string rawFilename)
    {
        var stem = Path.GetFileNameWithoutExtension(rawFilename);
        var match = NumberTokenPattern().Match(stem);
        if (!match.Success)
        {
            return null;
        }

        var start = int.Parse(match.Groups["start"].Value);

        return new ParseResult(
            ChapterNumber.ForChapter(start),
            Subtitle: null,
            nameof(BareNumberParser),
            ParseConfidence.Exact);
    }
}
