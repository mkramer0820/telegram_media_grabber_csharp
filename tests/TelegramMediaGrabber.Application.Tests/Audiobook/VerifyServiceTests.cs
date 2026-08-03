using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Application.Tests.Fakes;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Audiobook;

public class VerifyServiceTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ChannelOptions MakeChannel() =>
        new(
            Id: "@chan", Name: "chan", MediaTypes: [MediaType.Audio], OutputSubdir: "staging",
            MinDate: null, AudiobookMode: true,
            Metadata: new AudiobookMetadata("Some Author", "Some Novel"), Overrides: []);

    [Fact]
    public async Task RunChannelAsync_CorrectsMismatchedEpisodeNumber()
    {
        var dir = CreateTempDir();
        try
        {
            var destRoot = Path.Combine(dir, "Audiobooks");
            var channel = MakeChannel();
            var bookDir = AudiobookNaming.BookDir(destRoot, channel.Metadata!);
            Directory.CreateDirectory(bookDir);

            // Currently tagged (wrongly) as episode 999 — Telegram's own
            // raw filename says the truth is 1053.
            var wrongPath = Path.Combine(bookDir, "Some Novel - Ep 0999.mp3");
            File.WriteAllText(wrongPath, "x");

            var stateRepository = new FakeStateRepository();
            await stateRepository.RecordDownloadedFileAsync(42, 999, wrongPath);

            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(999, 42, DateTimeOffset.UtcNow, "1053.m4a", true, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesByKey: new() { [(42, 999)] = message });

            var service = new VerifyService(client, stateRepository, new AudiobookProcessingService(new FakeAudiobookTagger()));

            var summary = await service.RunChannelAsync(channel, destRoot);

            Assert.Equal(new VerifySummary(1, 1, 0), summary);
            Assert.False(File.Exists(wrongPath));
            var correctedPath = Path.Combine(bookDir, "Some Novel - Ep 1053.mp3");
            Assert.True(File.Exists(correctedPath));
            Assert.Equal((42L, 999), await stateRepository.FindDownloadedRecordByPathAsync(correctedPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunChannelAsync_LeavesAlreadyCorrectFileUntouched()
    {
        var dir = CreateTempDir();
        try
        {
            var destRoot = Path.Combine(dir, "Audiobooks");
            var channel = MakeChannel();
            var bookDir = AudiobookNaming.BookDir(destRoot, channel.Metadata!);
            Directory.CreateDirectory(bookDir);

            var correctPath = Path.Combine(bookDir, "Some Novel - Ep 0042.mp3");
            File.WriteAllText(correctPath, "x");

            var stateRepository = new FakeStateRepository();
            await stateRepository.RecordDownloadedFileAsync(42, 100, correctPath);

            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(100, 42, DateTimeOffset.UtcNow, "42.m4a", true, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesByKey: new() { [(42, 100)] = message });

            var service = new VerifyService(client, stateRepository, new AudiobookProcessingService(new FakeAudiobookTagger()));

            var summary = await service.RunChannelAsync(channel, destRoot);

            Assert.Equal(new VerifySummary(1, 0, 0), summary);
            Assert.True(File.Exists(correctPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunChannelAsync_SkipsWhenTelegramFilenameHasNoNumberEither()
    {
        var dir = CreateTempDir();
        try
        {
            var destRoot = Path.Combine(dir, "Audiobooks");
            var channel = MakeChannel();
            var bookDir = AudiobookNaming.BookDir(destRoot, channel.Metadata!);
            Directory.CreateDirectory(bookDir);

            var existingPath = Path.Combine(bookDir, "Some Novel - Ep 0999.mp3");
            File.WriteAllText(existingPath, "x");

            var stateRepository = new FakeStateRepository();
            await stateRepository.RecordDownloadedFileAsync(42, 999, existingPath);

            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(999, 42, DateTimeOffset.UtcNow, "random_name.m4a", true, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesByKey: new() { [(42, 999)] = message });

            var service = new VerifyService(client, stateRepository, new AudiobookProcessingService(new FakeAudiobookTagger()));

            var summary = await service.RunChannelAsync(channel, destRoot);

            Assert.Equal(new VerifySummary(1, 0, 0), summary);
            Assert.True(File.Exists(existingPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunChannelAsync_SkipsWhenLocalFileIsMissing()
    {
        var dir = CreateTempDir();
        try
        {
            var destRoot = Path.Combine(dir, "Audiobooks");
            var channel = MakeChannel();
            var missingPath = Path.Combine(AudiobookNaming.BookDir(destRoot, channel.Metadata!), "Some Novel - Ep 0999.mp3");

            var stateRepository = new FakeStateRepository();
            await stateRepository.RecordDownloadedFileAsync(42, 999, missingPath);

            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(999, 42, DateTimeOffset.UtcNow, "1053.m4a", true, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesByKey: new() { [(42, 999)] = message });

            var service = new VerifyService(client, stateRepository, new AudiobookProcessingService(new FakeAudiobookTagger()));

            var summary = await service.RunChannelAsync(channel, destRoot);

            Assert.Equal(new VerifySummary(0, 0, 0), summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VerifySummary_AdditionCombinesAllFields()
    {
        var a = new VerifySummary(1, 2, 3);
        var b = new VerifySummary(4, 5, 6);
        Assert.Equal(new VerifySummary(5, 7, 9), a + b);
    }
}
