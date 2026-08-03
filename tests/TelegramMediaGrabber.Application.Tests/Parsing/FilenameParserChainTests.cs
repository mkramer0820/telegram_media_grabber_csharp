using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Parsing;

/// <summary>
/// The real-world filename corpus discovered against the Python
/// predecessor's actual library (PROJECT_STATE.md §10), ported as the
/// day-one test suite per AGENTS.md §6.3.
/// </summary>
public class FilenameParserChainTests
{
    private readonly FilenameParserChain _chain = FilenameParserChain.Default;

    [Fact]
    public void ChapterPattern_WithSubtitleAndUploaderTag()
    {
        var result = _chain.TryParse("__Example Novel.Ep 2027 - The Strength of the Wolf-XtreamStories.mp3");

        Assert.NotNull(result);
        Assert.Equal(ContentUnitKind.Chapter, result.Number.Kind);
        Assert.Equal(2027, result.Number.Value);
        Assert.Equal("The Strength of the Wolf", result.Subtitle);
        Assert.Equal(nameof(ChapterPatternParser), result.MatchedBy);
    }

    [Theory]
    [InlineData("Episode 12 - The Beginning.mp3", 12, "The Beginning")]
    [InlineData("ep.5 - Something.mp3", 5, "Something")]
    [InlineData("Ep 42: The Return.mp3", 42, "The Return")]
    public void ChapterPattern_AcceptsVariants(string filename, int expectedEpisode, string expectedSubtitle)
    {
        var result = _chain.TryParse(filename);

        Assert.NotNull(result);
        Assert.Equal(expectedEpisode, result.Number.Value);
        Assert.Equal(expectedSubtitle, result.Subtitle);
    }

    [Fact]
    public void ChapterPattern_PreservesSpaceSeparatedTrailingNumberInSubtitle()
    {
        // "Part 2" is genuine subtitle content (space before the digit),
        // not an uploader signature, and must not be stripped.
        var result = _chain.TryParse("Ep 7 - Final Battle Part 2.mp3");

        Assert.NotNull(result);
        Assert.Equal("Final Battle Part 2", result.Subtitle);
    }

    [Fact]
    public void ChapterPattern_NoSubtitleTextYieldsNullSubtitle()
    {
        var result = _chain.TryParse("Ep 9 - .mp3");

        Assert.NotNull(result);
        Assert.Equal(9, result.Number.Value);
        Assert.Null(result.Subtitle);
    }

    [Theory]
    [InlineData("1114.m4a", 1114)]
    [InlineData("1114..m4a", 1114)] // trailing dot survives an upstream double-dot artifact
    public void BareNumber_ParsesWholeStemNumber(string filename, int expected)
    {
        var result = _chain.TryParse(filename);

        Assert.NotNull(result);
        Assert.Equal(ContentUnitKind.Chapter, result.Number.Kind);
        Assert.Equal(expected, result.Number.Value);
        Assert.Null(result.Subtitle);
        Assert.Equal(nameof(BareNumberParser), result.MatchedBy);
    }

    [Fact]
    public void BareNumber_RangeUsesStartNumber()
    {
        var result = _chain.TryParse("5-6.m4a");

        Assert.NotNull(result);
        Assert.Equal(5, result.Number.Value);
    }

    [Fact]
    public void BareNumber_TrailingRangeWithTitlePrefix()
    {
        var result = _chain.TryParse("Example Novel 1751-1846.m4a");

        Assert.NotNull(result);
        Assert.Equal(1751, result.Number.Value);
    }

    [Fact]
    public void BareNumber_LeadingRangeWithUnderscoreSeparatorAndTitleSuffix()
    {
        var result = _chain.TryParse("0001_0100_Another_Novel.mp3");

        Assert.NotNull(result);
        Assert.Equal(1, result.Number.Value);
    }

    [Fact]
    public void BareNumber_SecondUnderscoreSeparatedRangeExample()
    {
        var result = _chain.TryParse("0201_0300_Another_Novel.mp3");

        Assert.NotNull(result);
        Assert.Equal(201, result.Number.Value);
    }

    [Theory]
    [InlineData("random_upload_name.mp3")]
    [InlineData("totally_untitled_file.mp3")]
    public void UnparseableFilenames_ReturnNull(string filename)
    {
        Assert.Null(_chain.TryParse(filename));
    }

    [Fact]
    public void VolumePattern_WithSubtitle()
    {
        var result = _chain.TryParse("Example Novel Volume 10 Dark Lord's Dreadful Travelogue.m4a");

        Assert.NotNull(result);
        Assert.Equal(ContentUnitKind.Volume, result.Number.Kind);
        Assert.Equal(10, result.Number.Value);
        Assert.Equal("Dark Lord's Dreadful Travelogue", result.Subtitle);
        Assert.Equal(nameof(VolumePatternParser), result.MatchedBy);
    }

    [Theory]
    [InlineData("Example Novel Vol 3 Prince of Nothing.m4a")]
    [InlineData("Example Novel Vol. 3 Prince of Nothing.m4a")]
    public void VolumePattern_AcceptsAbbreviationAndPeriod(string filename)
    {
        var result = _chain.TryParse(filename);

        Assert.NotNull(result);
        Assert.Equal(3, result.Number.Value);
        Assert.Equal("Prince of Nothing", result.Subtitle);
    }

    [Fact]
    public void VolumePattern_NoTrailingSubtitleYieldsNullSubtitle()
    {
        var result = _chain.TryParse("Example Novel Volume 1.m4a");

        Assert.NotNull(result);
        Assert.Equal(1, result.Number.Value);
        Assert.Null(result.Subtitle);
    }

    [Fact]
    public void ChapterPattern_TakesPriorityOverVolumePattern()
    {
        // Contains "vol" as a substring inside "Evolving" but must still
        // be tagged as a chapter, not misread as a volume.
        var result = _chain.TryParse("Ep 5 - Evolving Powers.mp3");

        Assert.NotNull(result);
        Assert.Equal(ContentUnitKind.Chapter, result.Number.Kind);
        Assert.Equal(5, result.Number.Value);
        Assert.Equal(nameof(ChapterPatternParser), result.MatchedBy);
    }

    [Fact]
    public void VolumePattern_TakesPriorityOverBareNumberParser()
    {
        // Without the volume parser, the generic bare-number rule would
        // grab "10" here and silently mistag a whole volume as chapter 10
        // — the exact incident recorded in PROJECT_STATE.md §10.
        var result = _chain.TryParse("Example Novel Volume 10 Dark Lord's Dreadful Travelogue.m4a");

        Assert.NotNull(result);
        Assert.Equal(ContentUnitKind.Volume, result.Number.Kind);
    }
}
