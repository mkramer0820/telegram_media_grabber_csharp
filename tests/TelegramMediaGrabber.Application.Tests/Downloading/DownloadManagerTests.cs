using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Downloading;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Application.Tests.Fakes;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Downloading;

public class DownloadManagerTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dmt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ChannelOptions MakeChannel(string name = "chan", bool audiobookMode = false, AudiobookMetadata? metadata = null) =>
        new(
            Id: $"@{name}", Name: name, MediaTypes: [MediaType.Photo, MediaType.Video, MediaType.Document],
            OutputSubdir: name, MinDate: null, AudiobookMode: audiobookMode, Metadata: metadata, Overrides: []);

    private static DownloadManager MakeManager(
        string downloadRoot, FakeTelegramClient client, FakeStateRepository stateRepository,
        int maxConcurrent = 3, string? audiobooksDestDir = null) =>
        new(
            client, stateRepository, new AudiobookProcessingService(new FakeAudiobookTagger()),
            new ChapterParsingService(), downloadRoot, maxConcurrent, audiobooksDestDir: audiobooksDestDir);

    [Fact]
    public async Task RunAsync_DownloadsAndRecordsNewMessage()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "report.pdf", false, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();

            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([MakeChannel()]);

            var expectedPath = Path.Combine(dir, "chan", "report.pdf");
            Assert.True(File.Exists(expectedPath));
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "chan"), "*.tmp"));
            Assert.True(await stateRepository.IsDownloadedAsync(42, 1));
            Assert.Equal(1, await stateRepository.GetLastMessageIdAsync(42));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SkipsAlreadyDownloadedMessage()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "report.pdf", false, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();
            await stateRepository.RecordDownloadedFileAsync(42, 1, "already-there.pdf");

            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([MakeChannel()]);

            Assert.False(File.Exists(Path.Combine(dir, "chan", "report.pdf")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SkipsMessagesOlderThanMinDate()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var newMessage = new TelegramMessage(2, 42, new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), "new.pdf", false, false, false, true);
            var oldMessage = new TelegramMessage(1, 42, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "old.pdf", false, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [newMessage, oldMessage]); // newest-first, matching Telegram's real order
            var stateRepository = new FakeStateRepository();

            var channel = MakeChannel() with { MinDate = new DateOnly(2026, 6, 13) };
            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([channel]);

            Assert.True(File.Exists(Path.Combine(dir, "chan", "new.pdf")));
            Assert.False(File.Exists(Path.Combine(dir, "chan", "old.pdf")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_FiltersByMediaType()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var photoMessage = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "photo.jpg", false, false, true, false);
            var docMessage = new TelegramMessage(2, 42, DateTimeOffset.UtcNow, "doc.pdf", false, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [docMessage, photoMessage]);
            var stateRepository = new FakeStateRepository();

            var channel = MakeChannel() with { MediaTypes = [MediaType.Document] };
            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([channel]);

            Assert.True(File.Exists(Path.Combine(dir, "chan", "doc.pdf")));
            Assert.False(File.Exists(Path.Combine(dir, "chan", "photo.jpg")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RespectsMaxMessagesCap()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(55, "chan");
            // Newest-first order, matching Telegram's real order.
            var messages = new[]
            {
                new TelegramMessage(3, 55, DateTimeOffset.UtcNow, "newest.pdf", false, false, false, true),
                new TelegramMessage(2, 55, DateTimeOffset.UtcNow, "middle.pdf", false, false, false, true),
                new TelegramMessage(1, 55, DateTimeOffset.UtcNow, "oldest.pdf", false, false, false, true),
            };
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: messages.ToList());
            var stateRepository = new FakeStateRepository();

            var channel = MakeChannel() with { MaxMessages = 2 };
            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([channel]);

            Assert.True(File.Exists(Path.Combine(dir, "chan", "newest.pdf")));
            Assert.True(File.Exists(Path.Combine(dir, "chan", "middle.pdf")));
            Assert.False(File.Exists(Path.Combine(dir, "chan", "oldest.pdf")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_FiltersByEpisodeRange()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var inRange = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "Ep 22 - Title.mp3", true, false, false, false);
            var outOfRange = new TelegramMessage(2, 42, DateTimeOffset.UtcNow, "Ep 30 - Title.mp3", true, false, false, false);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [outOfRange, inRange]);
            var stateRepository = new FakeStateRepository();

            var channel = MakeChannel() with
            {
                MediaTypes = [MediaType.Audio],
                EpisodeRange = new EpisodeRangeOptions(20, 25),
            };
            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([channel]);

            Assert.True(File.Exists(Path.Combine(dir, "chan", "Ep 22 - Title.mp3")));
            Assert.False(File.Exists(Path.Combine(dir, "chan", "Ep 30 - Title.mp3")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_AutoUploadsToConfiguredTarget()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "report.pdf", false, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();

            var channel = MakeChannel() with { AutoUploadTarget = "@backup" };
            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([channel]);

            var finalPath = Path.Combine(dir, "chan", "report.pdf");
            Assert.Single(client.DocumentUploads, u => u.FilePath == finalPath);
            Assert.Contains("@backup", client.ResolvedIds);

            // Re-running is a no-op: the file is already dedup-marked uploaded.
            await manager.RunAsync([channel with { }]);
            Assert.Single(client.DocumentUploads);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WatchAsync_DownloadsPushedMessagesForWatchedChannelsOnly()
    {
        var dir = CreateTempDir();
        try
        {
            var watchedEntity = new TelegramEntity(42, "chan");
            var otherEntity = new TelegramEntity(99, "other");
            var wantedMessage = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "report.pdf", false, false, false, true);
            var unrelatedChatMessage = new TelegramMessage(2, 99, DateTimeOffset.UtcNow, "ignored.pdf", false, false, false, true);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = watchedEntity, ["@other"] = otherEntity })
            {
                MessagesToWatch = [unrelatedChatMessage, wantedMessage],
            };
            var stateRepository = new FakeStateRepository();
            var manager = MakeManager(dir, client, stateRepository);

            using var cts = new CancellationTokenSource();
            var watchTask = manager.WatchAsync([MakeChannel()], cts.Token);

            // WatchAsync only returns on cancellation (it's a live stream);
            // give the fake's queued messages a moment to flow through,
            // then stop it and assert on what landed before that point.
            await Task.Delay(200);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);

            Assert.True(File.Exists(Path.Combine(dir, "chan", "report.pdf")));
            Assert.False(Directory.Exists(Path.Combine(dir, "other")));
            Assert.True(await stateRepository.IsDownloadedAsync(42, 1));
            Assert.Equal(1, await stateRepository.GetLastMessageIdAsync(42));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_AudiobookModeTagsAndRelocatesFile()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "5.mp3", true, false, false, false);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();

            var metadata = new AudiobookMetadata("Some Author", "Some Novel");
            var channel = MakeChannel(audiobookMode: true, metadata: metadata) with { MediaTypes = [MediaType.Audio] };
            var manager = MakeManager(dir, client, stateRepository);
            await manager.RunAsync([channel]);

            var expectedPath = Path.Combine(
                AudiobookNaming.BookDir(Path.Combine(dir, "Audiobooks"), metadata), "Some Novel - Ep 0005.mp3");
            Assert.True(File.Exists(expectedPath));
            Assert.True(await stateRepository.IsDownloadedAsync(42, 1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Regression test: untitled files (no parsable episode number in
    /// their filename -- e.g. Telegram's own "Unknown Track" placeholder)
    /// used to get their inferred chapter number from whatever order
    /// concurrent downloads happened to *finish* in, not upload order.
    /// Real messages are scanned newest-first (mirrored here by listing
    /// them newest-first in <c>messagesToIterate</c>, same as the real
    /// client), so before the fix, an untitled file posted right after a
    /// properly-numbered "Ep 59" would very likely get processed (and thus
    /// numbered) *before* that anchor even started downloading, since
    /// nothing serialized chapter-number resolution ahead of the
    /// concurrent download dispatch. This asserts all three untitled
    /// files land immediately after their real "Ep 59" anchor, in the
    /// order they were actually posted (chronological/upload order) --
    /// regardless of scan order or concurrent completion order.
    /// </summary>
    [Fact]
    public async Task RunAsync_AudiobookMode_UntitledFiles_NumberedInUploadOrderAfterNearestAnchor()
    {
        var dir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var baseDate = DateTimeOffset.UtcNow;

            // Chronological (upload) order is oldest -> newest: anchor,
            // then three untitled files. Listed here newest-first (4,3,2,1)
            // to mirror the real client's actual scan order.
            var anchor = new TelegramMessage(1, 42, baseDate, "Ep 59 - Power of Ki.mp3", true, false, false, false);
            var untitled1 = new TelegramMessage(2, 42, baseDate.AddMinutes(1), "Unknown Track.mp3", true, false, false, false);
            var untitled2 = new TelegramMessage(3, 42, baseDate.AddMinutes(2), "Unknown Track (2).mp3", true, false, false, false);
            var untitled3 = new TelegramMessage(4, 42, baseDate.AddMinutes(3), "Unknown Track (3).mp3", true, false, false, false);

            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [untitled3, untitled2, untitled1, anchor]);
            var stateRepository = new FakeStateRepository();

            var metadata = new AudiobookMetadata("Some Author", "Some Novel");
            var channel = MakeChannel(audiobookMode: true, metadata: metadata) with { MediaTypes = [MediaType.Audio] };
            // maxConcurrent > 1 so the four downloads genuinely race for
            // completion order, not just dispatch order.
            var manager = MakeManager(dir, client, stateRepository, maxConcurrent: 4);
            await manager.RunAsync([channel]);

            var bookDir = AudiobookNaming.BookDir(Path.Combine(dir, "Audiobooks"), metadata);
            // The anchor keeps its parsed subtitle; the three untitled
            // files that follow it chronologically have none.
            Assert.True(File.Exists(Path.Combine(bookDir, "Some Novel - Ep 0059 - Power of Ki.mp3")));
            Assert.True(File.Exists(Path.Combine(bookDir, "Some Novel - Ep 0060.mp3")));
            Assert.True(File.Exists(Path.Combine(bookDir, "Some Novel - Ep 0061.mp3")));
            Assert.True(File.Exists(Path.Combine(bookDir, "Some Novel - Ep 0062.mp3")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Regression test: DownloadManager used to hardcode "{downloadRoot}/Audiobooks"
    /// as the tagged-file destination regardless of what LOCAL_MEDIA_SERVER
    /// (a configured, possibly external, media-server path) was set to --
    /// every real download/watch/run invocation silently ignored it, even
    /// though --mode reprocess/verify honored it correctly. Caught live
    /// against a real Plex library.
    /// </summary>
    [Fact]
    public async Task RunAsync_AudiobookModeUsesConfiguredDestDir_NotTheDownloadRootDefault()
    {
        var dir = CreateTempDir();
        var externalMediaServerDir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "5.mp3", true, false, false, false);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();

            var metadata = new AudiobookMetadata("Some Author", "Some Novel");
            var channel = MakeChannel(audiobookMode: true, metadata: metadata) with { MediaTypes = [MediaType.Audio] };
            var manager = MakeManager(dir, client, stateRepository, audiobooksDestDir: externalMediaServerDir);
            await manager.RunAsync([channel]);

            var expectedPath = Path.Combine(
                AudiobookNaming.BookDir(externalMediaServerDir, metadata), "Some Novel - Ep 0005.mp3");
            Assert.True(File.Exists(expectedPath));
            Assert.False(Directory.Exists(Path.Combine(dir, "Audiobooks")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(externalMediaServerDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_LocalOnlyChannel_StaysUnderDownloadRoot_IgnoresConfiguredDestDir()
    {
        var dir = CreateTempDir();
        var externalMediaServerDir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "5.mp3", true, false, false, false);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();

            var metadata = new AudiobookMetadata("Some Author", "Some Novel");
            var channel = MakeChannel(audiobookMode: true, metadata: metadata) with
            {
                MediaTypes = [MediaType.Audio],
                LocalOnly = true,
            };
            var manager = MakeManager(dir, client, stateRepository, audiobooksDestDir: externalMediaServerDir);
            await manager.RunAsync([channel]);

            var expectedPath = Path.Combine(
                AudiobookNaming.BookDir(Path.Combine(dir, "Audiobooks"), metadata), "Some Novel - Ep 0005.mp3");
            Assert.True(File.Exists(expectedPath));
            Assert.Empty(Directory.GetFileSystemEntries(externalMediaServerDir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(externalMediaServerDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MediaServerSubdirOverride_UsedInsteadOfNovelTitle()
    {
        var dir = CreateTempDir();
        var externalMediaServerDir = CreateTempDir();
        try
        {
            var entity = new TelegramEntity(42, "chan");
            var message = new TelegramMessage(1, 42, DateTimeOffset.UtcNow, "5.mp3", true, false, false, false);
            var client = new FakeTelegramClient(
                entitiesById: new() { ["@chan"] = entity },
                messagesToIterate: [message]);
            var stateRepository = new FakeStateRepository();

            var metadata = new AudiobookMetadata("Some Author", "Some Novel");
            var channel = MakeChannel(audiobookMode: true, metadata: metadata) with
            {
                MediaTypes = [MediaType.Audio],
                MediaServerSubdir = "Custom Folder Name",
            };
            var manager = MakeManager(dir, client, stateRepository, audiobooksDestDir: externalMediaServerDir);
            await manager.RunAsync([channel]);

            var expectedPath = Path.Combine(externalMediaServerDir, "Custom Folder Name", "Some Novel - Ep 0005.mp3");
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(externalMediaServerDir, recursive: true);
        }
    }
}
