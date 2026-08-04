using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Audiobook;

public sealed class AudiobookNamingTests
{
    private static ChannelOptions MakeChannel(bool localOnly = false, string? mediaServerSubdir = null) => new(
        Id: "@chan", Name: "chan", MediaTypes: [MediaType.Audio], OutputSubdir: "chan",
        MinDate: null, AudiobookMode: true,
        Metadata: new AudiobookMetadata("Some Author", "Some Novel"),
        Overrides: [], LocalOnly: localOnly, MediaServerSubdir: mediaServerSubdir);

    [Fact]
    public void BookDir_HasNoAuthorSegment_UsesTitleOnly()
    {
        var metadata = new AudiobookMetadata("Some Author", "Some Novel");

        var dir = AudiobookNaming.BookDir("D:/plex/audio", metadata);

        Assert.Equal(Path.Combine("D:/plex/audio", "Some Novel"), dir);
        Assert.DoesNotContain("Some Author", dir);
    }

    [Fact]
    public void BookDir_UsesSubdirOverride_WhenGiven()
    {
        var metadata = new AudiobookMetadata("Some Author", "Some Novel");

        var dir = AudiobookNaming.BookDir("D:/plex/audio", metadata, subdirOverride: "Custom Folder Name");

        Assert.Equal(Path.Combine("D:/plex/audio", "Custom Folder Name"), dir);
    }

    [Fact]
    public void EffectiveDestRoot_UsesConfiguredDestDir_WhenNotLocalOnly()
    {
        var channel = MakeChannel(localOnly: false);

        var effective = AudiobookNaming.EffectiveDestRoot(channel, "downloads", "D:/plex/audio");

        Assert.Equal("D:/plex/audio", effective);
    }

    [Fact]
    public void EffectiveDestRoot_UsesDownloadRootAudiobooks_WhenLocalOnly()
    {
        var channel = MakeChannel(localOnly: true);

        var effective = AudiobookNaming.EffectiveDestRoot(channel, "downloads", "D:/plex/audio");

        Assert.Equal(Path.Combine("downloads", "Audiobooks"), effective);
    }
}
