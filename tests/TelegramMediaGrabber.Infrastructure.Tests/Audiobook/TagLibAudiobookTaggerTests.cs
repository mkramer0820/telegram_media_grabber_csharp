using System.Diagnostics;
using TelegramMediaGrabber.Domain;
using TelegramMediaGrabber.Infrastructure.Audiobook;

namespace TelegramMediaGrabber.Infrastructure.Tests.Audiobook;

/// <summary>
/// Tests for <see cref="TagLibAudiobookTagger"/> against real (tiny,
/// synthetically generated) mp3/m4a/m4b fixture files — TagLibSharp needs
/// an actual valid container to parse, so fixtures are generated at test
/// time with <c>ffmpeg</c> (must be on PATH) rather than hand-built or
/// checked in as binaries. Tests requiring ffmpeg skip gracefully if it
/// isn't available in the environment running them.
/// </summary>
public sealed class TagLibAudiobookTaggerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("tagger-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(5000);
            return process is { ExitCode: 0 };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private string GenerateFixture(string extension, string codecArgs)
    {
        var path = Path.Combine(_tempDir, $"fixture{extension}");
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in $"-y -f lavfi -i sine=frequency=440:duration=1 -ar 44100 {codecArgs} \"{path}\"".Split(' '))
        {
            psi.ArgumentList.Add(arg.Trim('"'));
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        process.WaitForExit(15000);
        if (process.ExitCode != 0 || !File.Exists(path))
        {
            throw new InvalidOperationException($"ffmpeg failed to generate fixture '{path}' (exit {process.ExitCode}).");
        }

        return path;
    }

    [Fact]
    public void Tag_throws_NotSupportedException_for_an_unsupported_extension()
    {
        var path = Path.Combine(_tempDir, "not_audio.txt");
        File.WriteAllText(path, "hello");

        var tagger = new TagLibAudiobookTagger();
        var metadata = new AudiobookMetadata("Author", "Title");
        var info = new ParseResult(ChapterNumber.ForChapter(1), null, "Test", ParseConfidence.Exact);

        Assert.Throws<NotSupportedException>(() => tagger.Tag(path, metadata, info));
    }

    [Fact]
    public void Tag_writes_and_round_trips_ID3_tags_on_an_mp3_file()
    {
        if (!FfmpegAvailable())
        {
            return; // Environment has no ffmpeg; nothing to verify against here.
        }

        var path = GenerateFixture(".mp3", "-b:a 64k");
        var metadata = new AudiobookMetadata("Example Author", "Example Novel");
        var info = new ParseResult(ChapterNumber.ForChapter(247), "The Real Title", "Test", ParseConfidence.Exact);

        new TagLibAudiobookTagger().Tag(path, metadata, info);

        using var reread = TagLib.File.Create(path);
        Assert.Equal("Example Author", reread.Tag.FirstPerformer);
        Assert.Equal("Example Author", reread.Tag.FirstAlbumArtist);
        Assert.Equal("Example Novel", reread.Tag.Album);
        Assert.Equal(247u, reread.Tag.Track);
        Assert.Equal("Ep 247 - The Real Title", reread.Tag.Title);
    }

    [Theory]
    [InlineData(".m4a")]
    [InlineData(".m4b")]
    public void Tag_writes_and_round_trips_MP4_atom_tags(string extension)
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var path = GenerateFixture(extension, "-c:a aac -b:a 64k");
        var metadata = new AudiobookMetadata("Some Author", "Some Novel");
        var info = new ParseResult(ChapterNumber.ForVolume(2), null, "Test", ParseConfidence.Exact);

        new TagLibAudiobookTagger().Tag(path, metadata, info);

        using var reread = TagLib.File.Create(path);
        Assert.Equal("Some Author", reread.Tag.FirstPerformer);
        Assert.Equal("Some Author", reread.Tag.FirstAlbumArtist);
        Assert.Equal("Some Novel", reread.Tag.Album);
        Assert.Equal(2u, reread.Tag.Track);
        Assert.Equal("Some Novel - Vol 2", reread.Tag.Title);
    }
}
