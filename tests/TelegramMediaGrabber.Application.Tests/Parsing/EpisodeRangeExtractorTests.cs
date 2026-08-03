using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Parsing;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Parsing;

public sealed class EpisodeRangeExtractorTests
{
    [Theory]
    [InlineData("Ep 2027 - The Strength of the Wolf.mp3", 2027, 2027)]
    [InlineData("Ep 1012-1058.mp3", 1012, 1058)]
    [InlineData("Episode 1012-1058.m4a", 1012, 1058)]
    [InlineData("1114.m4a", 1114, 1114)]
    [InlineData("5-6.m4a", 5, 6)]
    [InlineData("Example Novel 1751-1846.m4a", 1751, 1846)]
    [InlineData("0001_0100_Another_Novel.mp3", 1, 100)]
    public void TryExtract_reads_expected_range(string filename, int expectedStart, int expectedEnd)
    {
        var result = EpisodeRangeExtractor.TryExtract(filename);

        Assert.Equal((expectedStart, expectedEnd), result);
    }

    [Fact]
    public void TryExtract_returns_null_for_pure_text_filename()
    {
        Assert.Null(EpisodeRangeExtractor.TryExtract("cover-art.jpg"));
    }

    [Fact]
    public void WantsEpisode_returns_true_when_no_range_configured()
    {
        Assert.True(EpisodeRangeExtractor.WantsEpisode(null, "Ep 5 - Title.mp3"));
    }

    [Fact]
    public void WantsEpisode_returns_true_for_unparseable_filename_conservative_default()
    {
        var range = new EpisodeRangeOptions(20, 25);

        Assert.True(EpisodeRangeExtractor.WantsEpisode(range, "cover-art.jpg"));
    }

    [Theory]
    [InlineData("Ep 22 - Title.mp3", true)]   // fully inside
    [InlineData("Ep 20 - Title.mp3", true)]   // at start boundary
    [InlineData("Ep 25 - Title.mp3", true)]   // at end boundary
    [InlineData("Ep 26 - Title.mp3", false)]  // just outside
    [InlineData("Ep 19 - Title.mp3", false)]  // just outside
    public void WantsEpisode_checks_single_episode_membership(string filename, bool expected)
    {
        var range = new EpisodeRangeOptions(20, 25);

        Assert.Equal(expected, EpisodeRangeExtractor.WantsEpisode(range, filename));
    }

    [Theory]
    [InlineData("Example Novel 15-22.mp3", true)]   // bundle overlaps requested range's start
    [InlineData("Example Novel 24-30.mp3", true)]   // bundle overlaps requested range's end
    [InlineData("Example Novel 100-251.mp3", false)] // bundle entirely outside
    public void WantsEpisode_checks_range_overlap_for_bundle_files(string filename, bool expected)
    {
        var range = new EpisodeRangeOptions(20, 25);

        Assert.Equal(expected, EpisodeRangeExtractor.WantsEpisode(range, filename));
    }
}
