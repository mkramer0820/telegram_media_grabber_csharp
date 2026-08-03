using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Parsing;

public class ChannelOverrideLookupTests
{
    [Fact]
    public void TryGetOverride_ReturnsConfiguredNumberAndSubtitle()
    {
        var lookup = new ChannelOverrideLookup(
        [
            new OverrideEntry("weird_upload_name_247.mp3", Skip: false, ContentUnitKind.Chapter, 247, "The Real Title"),
        ]);

        var result = lookup.TryGetOverride("weird_upload_name_247.mp3");

        Assert.NotNull(result);
        Assert.Equal(247, result.Number.Value);
        Assert.Equal(ContentUnitKind.Chapter, result.Number.Kind);
        Assert.Equal("The Real Title", result.Subtitle);
        Assert.Equal(ParseConfidence.Override, result.Confidence);
    }

    [Fact]
    public void TryGetOverride_ReturnsNullForUnknownFilename()
    {
        var lookup = new ChannelOverrideLookup([]);
        Assert.Null(lookup.TryGetOverride("anything.mp3"));
    }

    [Fact]
    public void ShouldSkip_TrueOnlyForSkipEntries()
    {
        var lookup = new ChannelOverrideLookup(
        [
            new OverrideEntry("duplicate_upload.mp3", Skip: true, null, null, null),
            new OverrideEntry("keep_this.mp3", Skip: false, ContentUnitKind.Chapter, 1, null),
        ]);

        Assert.True(lookup.ShouldSkip("duplicate_upload.mp3"));
        Assert.False(lookup.ShouldSkip("keep_this.mp3"));
        Assert.False(lookup.ShouldSkip("unrelated.mp3"));
    }

    [Fact]
    public void TryGetOverride_NeverReturnsResultForSkipEntry()
    {
        var lookup = new ChannelOverrideLookup(
        [
            new OverrideEntry("duplicate_upload.mp3", Skip: true, null, null, null),
        ]);

        Assert.Null(lookup.TryGetOverride("duplicate_upload.mp3"));
    }

    [Fact]
    public void Constructor_ThrowsOnDuplicateMatchFilename()
    {
        var entries = new[]
        {
            new OverrideEntry("same.mp3", Skip: false, ContentUnitKind.Chapter, 1, null),
            new OverrideEntry("same.mp3", Skip: false, ContentUnitKind.Chapter, 2, null),
        };

        Assert.Throws<InvalidOperationException>(() => new ChannelOverrideLookup(entries));
    }

    [Fact]
    public void Constructor_ThrowsWhenSkipEntryAlsoSpecifiesKindOrNumber()
    {
        var entries = new[]
        {
            new OverrideEntry("bad.mp3", Skip: true, ContentUnitKind.Chapter, 1, null),
        };

        Assert.Throws<InvalidOperationException>(() => new ChannelOverrideLookup(entries));
    }

    [Fact]
    public void Constructor_ThrowsWhenNonSkipEntryMissingKindOrNumber()
    {
        var entries = new[]
        {
            new OverrideEntry("bad.mp3", Skip: false, null, null, null),
        };

        Assert.Throws<InvalidOperationException>(() => new ChannelOverrideLookup(entries));
    }
}
