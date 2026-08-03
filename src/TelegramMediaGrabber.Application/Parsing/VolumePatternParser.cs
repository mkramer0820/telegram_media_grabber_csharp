using System.Text.RegularExpressions;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Matches "Volume 10 Dark Lord's Dreadful Travelogue" (case-insensitive,
/// "Vol"/"Vol."/"Volume" all accepted), capturing the volume number and
/// everything after it as a subtitle. A volume is a whole compiled book
/// bundling many chapters into one file — it MUST NOT be tagged/numbered
/// as if it were a single chapter, so this produces a
/// <see cref="ContentUnitKind.Volume"/> number, a completely separate
/// number/label space from <see cref="ChapterPatternParser"/>'s output.
/// Tried after <see cref="ChapterPatternParser"/> (an "Ep n" match always
/// wins) and before <see cref="BareNumberParser"/> (otherwise a volume's
/// number would be swallowed by the generic bare-number rule and silently
/// mistagged as a chapter — this happened once in the Python predecessor,
/// see PROJECT_STATE.md §10).
/// </summary>
public sealed partial class VolumePatternParser : IFilenameParser
{
    [GeneratedRegex(@"\bvol(?:ume)?\.?\s*(?<volume>\d+)\b\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePattern();

    public ParseResult? TryParse(string rawFilename)
    {
        var stem = Path.GetFileNameWithoutExtension(rawFilename);
        var match = VolumePattern().Match(stem);
        if (!match.Success)
        {
            return null;
        }

        var volume = int.Parse(match.Groups["volume"].Value);
        var subtitle = match.Groups["rest"].Value.Trim();

        return new ParseResult(
            ChapterNumber.ForVolume(volume),
            string.IsNullOrEmpty(subtitle) ? null : subtitle,
            nameof(VolumePatternParser),
            ParseConfidence.Exact);
    }
}
