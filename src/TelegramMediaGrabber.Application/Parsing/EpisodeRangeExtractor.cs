using System.Text.RegularExpressions;
using TelegramMediaGrabber.Application.Configuration;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Pre-download filtering by episode number/range (<see cref="EpisodeRangeOptions"/>
/// on <c>ChannelOptions</c>). A distinct, narrower concern from
/// <see cref="FilenameParserChain"/>/<see cref="ChapterParsingService"/>,
/// which answer "what is this file's canonical episode number, for
/// tagging/renaming" — this class only answers "does this filename look
/// like it falls inside the requested range, well enough to bother
/// downloading it". Deliberately three patterns, tried in a fixed order,
/// not a general-purpose numbering grammar: a filename this can't
/// confidently read is treated as in-range (see <see cref="WantsEpisode"/>),
/// so an <c>episode_range</c> filter can only ever narrow a download, never
/// silently drop a file it couldn't classify.
/// </summary>
public static partial class EpisodeRangeExtractor
{
    // "Ep 1012-1058" / "Episode 1012-1058": an explicit range, e.g. a
    // single bundled file covering many chapters. Both sides must be bare
    // digits with nothing but a separator/end-of-stem after -- "Ep 12 -
    // Title" falls through to EpisodeSinglePattern instead, exactly like
    // ChapterPatternParser treats non-numeric "rest" as a subtitle, not a
    // range end.
    [GeneratedRegex(@"ep(?:isode)?\.?\s*(?<start>\d+)\s*-\s*(?<end>\d+)\s*(?:$|[\s_.])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRangePattern();

    // "Ep 2027 - The Strength of the Wolf": a single chapter, ignoring
    // whatever subtitle text follows the number.
    [GeneratedRegex(@"ep(?:isode)?\.?\s*(?<episode>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeSinglePattern();

    // A cleanly-delimited bare number or number range anywhere in the
    // filename stem, e.g. "1114", "5-6", "Example Novel 100-251",
    // "0001_0100_Title" -- same shape as BareNumberParser, kept as a
    // separate copy deliberately: this class's job (range-membership
    // filtering) is different enough from tagging that sharing one
    // pattern across both would couple two things that should be free to
    // diverge.
    [GeneratedRegex(@"(?:^|[\s_-])(?<start>\d+)(?:[-_](?<end>\d+))?(?=$|[\s_.-])")]
    private static partial Regex NumberTokenPattern();

    /// <summary>
    /// Best-effort inclusive (start, end) episode numbers a filename
    /// covers. Tries three patterns in order and returns the first match.
    /// </summary>
    /// <param name="rawFilename">The original filename, with or without extension.</param>
    /// <returns>
    /// <c>(start, end)</c> with <c>start &lt;= end</c>, or <c>null</c> if
    /// no pattern matched.
    /// </returns>
    public static (int Start, int End)? TryExtract(string rawFilename)
    {
        var stem = Path.GetFileNameWithoutExtension(rawFilename);

        var rangeMatch = EpisodeRangePattern().Match(stem);
        if (rangeMatch.Success)
        {
            return Normalize(int.Parse(rangeMatch.Groups["start"].Value), int.Parse(rangeMatch.Groups["end"].Value));
        }

        var singleMatch = EpisodeSinglePattern().Match(stem);
        if (singleMatch.Success)
        {
            var episode = int.Parse(singleMatch.Groups["episode"].Value);
            return (episode, episode);
        }

        var numberMatch = NumberTokenPattern().Match(stem);
        if (numberMatch.Success)
        {
            var start = int.Parse(numberMatch.Groups["start"].Value);
            var end = numberMatch.Groups["end"].Success ? int.Parse(numberMatch.Groups["end"].Value) : start;
            return Normalize(start, end);
        }

        return null;
    }

    /// <summary>
    /// Decides whether a file should be downloaded given a channel's
    /// configured episode range.
    /// </summary>
    /// <param name="episodeRange">The channel's configured range, or <c>null</c> if unset (everything is wanted).</param>
    /// <param name="rawFilename">The message's raw filename.</param>
    /// <returns>
    /// <c>true</c> if no range is configured, the filename doesn't parse
    /// to a recognizable episode number at all (conservative default --
    /// never silently drop an unreadable file), or its episode
    /// number/range overlaps the configured range.
    /// </returns>
    public static bool WantsEpisode(EpisodeRangeOptions? episodeRange, string rawFilename)
    {
        if (episodeRange is null)
        {
            return true;
        }

        var parsed = TryExtract(rawFilename);
        if (parsed is not { } range)
        {
            return true;
        }

        return range.End >= episodeRange.Start && range.Start <= episodeRange.End;
    }

    private static (int Start, int End) Normalize(int a, int b) => a <= b ? (a, b) : (b, a);
}
