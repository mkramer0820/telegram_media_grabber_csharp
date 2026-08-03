using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Tests.Fakes;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Audiobook;

public class AudiobookProcessingServiceTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void InferNextEpisodeNumber_IsOneWhenDestDirMissing()
    {
        var number = AudiobookProcessingService.InferNextEpisodeNumber(@"C:\does\not\exist");
        Assert.Equal(1, number.Value);
        Assert.Equal(ContentUnitKind.Chapter, number.Kind);
    }

    [Fact]
    public void InferNextEpisodeNumber_IsOnePastHighestExistingEpisode()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Example Novel - Ep 0007 - Title.mp3"), "x");
            File.WriteAllText(Path.Combine(dir, "Example Novel - Ep 0012.mp3"), "x");
            File.WriteAllText(Path.Combine(dir, "cover.jpg"), "x"); // no "Ep n" -> ignored

            Assert.Equal(13, AudiobookProcessingService.InferNextEpisodeNumber(dir).Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InferNextEpisodeNumber_IgnoresVolumeTaggedFiles()
    {
        // Volume numbering must never leak into chapter-number inference.
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Example Novel - Vol 99.mp3"), "x");

            Assert.Equal(1, AudiobookProcessingService.InferNextEpisodeNumber(dir).Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseTaggedEpisodeNumber_ReturnsNullForUntaggedFile()
    {
        Assert.Null(AudiobookProcessingService.ParseTaggedEpisodeNumber("cover.jpg"));
    }

    [Fact]
    public void ApplyTagging_UsesGivenInfoNotFilenamesOwnNumber()
    {
        // The source filename claims episode 999 — ApplyTagging must
        // ignore that and use the explicitly-passed ParseResult instead.
        // This is exactly the property VerifyService relies on to correct
        // a file that's already mistagged under the wrong number.
        var dir = CreateTempDir();
        try
        {
            var source = Path.Combine(dir, "staging", "Ep 999 - Wrong.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "x");

            var tagger = new FakeAudiobookTagger();
            var service = new AudiobookProcessingService(tagger);
            var metadata = new AudiobookMetadata("Example Author", "Example Novel");
            var correctInfo = new ParseResult(ChapterNumber.ForChapter(5), "The Real One", "Test", ParseConfidence.Exact);

            var resultPath = service.ApplyTagging(source, correctInfo, metadata, Path.Combine(dir, "Audiobooks"));

            Assert.Equal("Example Novel - Ep 0005 - The Real One.mp3", Path.GetFileName(resultPath));
            Assert.False(File.Exists(source));
            Assert.Single(tagger.Calls);
            Assert.Equal(5, tagger.Calls[0].Info.Number.Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyTagging_NeverOverwritesDistinctExistingFile()
    {
        var dir = CreateTempDir();
        try
        {
            var destRoot = Path.Combine(dir, "Audiobooks");
            var metadata = new AudiobookMetadata("Example Author", "Example Novel");
            var existingDir = Path.Combine(AudiobookNaming.BookDir(destRoot, metadata));
            Directory.CreateDirectory(existingDir);
            var existing = Path.Combine(existingDir, "Example Novel - Ep 0001.mp3");
            File.WriteAllText(existing, "original content");

            var source = Path.Combine(dir, "1.mp3");
            File.WriteAllText(source, "new content");

            var service = new AudiobookProcessingService(new FakeAudiobookTagger());
            var info = new ParseResult(ChapterNumber.ForChapter(1), null, "Test", ParseConfidence.Exact);

            var resultPath = service.ApplyTagging(source, info, metadata, destRoot);

            Assert.NotEqual(existing, resultPath);
            Assert.Equal("Example Novel - Ep 0001 (1).mp3", Path.GetFileName(resultPath));
            Assert.Equal("original content", File.ReadAllText(existing));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
