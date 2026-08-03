using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.Tests.Fakes;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Audiobook;

public class ReprocessServiceTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ChannelOptions MakeChannel(string outputSubdir) =>
        new(
            Id: "@chan", Name: "chan", MediaTypes: [MediaType.Audio], OutputSubdir: outputSubdir,
            MinDate: null, AudiobookMode: true,
            Metadata: new AudiobookMetadata("Some Author", "Some Novel"), Overrides: []);

    private static (ReprocessService Service, FakeStateRepository StateRepository) MakeService()
    {
        var stateRepository = new FakeStateRepository();
        var audiobookProcessor = new AudiobookProcessingService(new FakeAudiobookTagger());
        var parsingService = new ChapterParsingService();
        return (new ReprocessService(stateRepository, audiobookProcessor, parsingService), stateRepository);
    }

    [Fact]
    public async Task RunAsync_ReprocessesStuckFileWithMatchingRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var downloadRoot = Path.Combine(dir, "downloads");
            var staging = Path.Combine(downloadRoot, "staging");
            Directory.CreateDirectory(staging);
            var stuckFile = Path.Combine(staging, "5.mp3");
            File.WriteAllText(stuckFile, "x");

            var (service, stateRepository) = MakeService();
            await stateRepository.RecordDownloadedFileAsync(1, 100, stuckFile);

            var destRoot = Path.Combine(dir, "Audiobooks");
            var summary = await service.RunAsync([MakeChannel("staging")], downloadRoot, destRoot);

            Assert.Equal(new ReprocessSummary(1, 0, 0), summary);
            Assert.False(File.Exists(stuckFile));

            var newPath = Path.Combine(AudiobookNaming.BookDir(destRoot, MakeChannel("staging").Metadata!), "Some Novel - Ep 0005.mp3");
            Assert.True(File.Exists(newPath));
            Assert.Equal((1L, 100), await stateRepository.FindDownloadedRecordByPathAsync(newPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ProcessesStuckFileWithNoMatchingRecordAndReportsWithoutRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var downloadRoot = Path.Combine(dir, "downloads");
            var staging = Path.Combine(downloadRoot, "staging");
            Directory.CreateDirectory(staging);
            var stuckFile = Path.Combine(staging, "5.mp3");
            File.WriteAllText(stuckFile, "x");

            var (service, _) = MakeService();
            var destRoot = Path.Combine(dir, "Audiobooks");

            var summary = await service.RunAsync([MakeChannel("staging")], downloadRoot, destRoot);

            Assert.Equal(new ReprocessSummary(0, 1, 0), summary);
            Assert.False(File.Exists(stuckFile));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CountsErrorAndContinuesOnUnsupportedExtension()
    {
        var dir = CreateTempDir();
        try
        {
            var downloadRoot = Path.Combine(dir, "downloads");
            var staging = Path.Combine(downloadRoot, "staging");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "cover.jpg"), "x");
            File.WriteAllText(Path.Combine(staging, "5.mp3"), "x");

            var (service, _) = MakeService();
            var destRoot = Path.Combine(dir, "Audiobooks");

            var summary = await service.RunAsync([MakeChannel("staging")], downloadRoot, destRoot);

            Assert.Equal(1, summary.Errors);
            Assert.Equal(1, summary.ProcessedWithoutRecord);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
