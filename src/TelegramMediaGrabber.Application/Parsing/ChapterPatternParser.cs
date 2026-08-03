using System.Text.RegularExpressions;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Matches "Ep 2027 - The Strength of the Wolf" (case-insensitive,
/// "Ep."/"Ep"/"Episode" all accepted, "-" or ":" separator) anywhere in
/// the filename stem, capturing the episode number and everything after
/// the separator as a candidate subtitle. Tried first in the chain — this
/// is the most explicit, least ambiguous shape.
/// </summary>
public sealed partial class ChapterPatternParser : IFilenameParser
{
    // Peels a trailing "-UploaderTag" signature (a hyphen directly
    // followed by a single space-free token at the very end) off an
    // extracted subtitle, e.g. "The Strength of the Wolf-XtreamStories"
    // -> "The Strength of the Wolf". Deliberately requires NO space
    // before the hyphen so genuine subtitle text like "...Part 2"
    // (space-separated) is left untouched.
    [GeneratedRegex(@"^(?<subtitle>.+?)-[A-Za-z0-9]+$")]
    private static partial Regex TrailingUploaderTagPattern();

    [GeneratedRegex(@"ep(?:isode)?\.?\s*(?<episode>\d+)\s*[-:]\s*(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();

    public ParseResult? TryParse(string rawFilename)
    {
        var stem = Path.GetFileNameWithoutExtension(rawFilename);
        var match = EpisodePattern().Match(stem);
        if (!match.Success)
        {
            return null;
        }

        var episode = int.Parse(match.Groups["episode"].Value);
        var rest = match.Groups["rest"].Value.Trim();

        var tagMatch = TrailingUploaderTagPattern().Match(rest);
        var subtitle = tagMatch.Success ? tagMatch.Groups["subtitle"].Value.Trim() : rest;

        return new ParseResult(
            ChapterNumber.ForChapter(episode),
            string.IsNullOrEmpty(subtitle) ? null : subtitle,
            nameof(ChapterPatternParser),
            ParseConfidence.Exact);
    }
}
