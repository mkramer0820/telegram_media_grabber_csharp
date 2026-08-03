using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Application.Progress;
using TelegramMediaGrabber.Application.Tests.Fakes;
using TelegramMediaGrabber.Application.Uploading;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Uploading;

public class UploadManagerTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void BuildQueue_NonRecursive_SkipsSubdirectoryFiles()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "top.txt"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "nested"));
            File.WriteAllText(Path.Combine(dir, "nested", "deep.txt"), "x");

            var manager = new UploadManager(new FakeTelegramClient(), new FakeStateRepository());
            var queue = manager.BuildQueue([new UploadJobOptions(dir, "@chan", Recursive: false)]);

            Assert.Single(queue);
            Assert.Equal("top.txt", Path.GetFileName(queue[0].FilePath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildQueue_Recursive_IncludesSubdirectoryFiles()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "top.txt"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "nested"));
            File.WriteAllText(Path.Combine(dir, "nested", "deep.txt"), "x");

            var manager = new UploadManager(new FakeTelegramClient(), new FakeStateRepository());
            var queue = manager.BuildQueue([new UploadJobOptions(dir, "@chan", Recursive: true)]);

            Assert.Equal(2, queue.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildQueue_MissingSourceDir_YieldsNoItemsForThatJob()
    {
        var manager = new UploadManager(new FakeTelegramClient(), new FakeStateRepository());
        var queue = manager.BuildQueue([new UploadJobOptions(@"C:\does\not\exist", "@chan", Recursive: false)]);

        Assert.Empty(queue);
    }

    [Fact]
    public async Task ProcessQueueAsync_UploadsAllFilesInOneBatch()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.txt"), "a"u8.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "b.txt"), "b"u8.ToArray());

            var client = new FakeTelegramClient();
            var manager = new UploadManager(client, new FakeStateRepository());
            var queue = manager.BuildQueue([new UploadJobOptions(dir, "@chan", Recursive: false)]);

            await manager.ProcessQueueAsync(queue);

            Assert.Single(client.MediaGroupUploads);
            Assert.Equal(2, client.MediaGroupUploads[0].FilePaths.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessQueueAsync_ChunksBatchesToMediaGroupMaxSize()
    {
        var dir = CreateTempDir();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{i:D2}.txt"), "x"u8.ToArray());
            }

            var client = new FakeTelegramClient();
            var manager = new UploadManager(client, new FakeStateRepository());
            var queue = manager.BuildQueue([new UploadJobOptions(dir, "@chan", Recursive: false)]);

            await manager.ProcessQueueAsync(queue);

            Assert.Equal(2, client.MediaGroupUploads.Count);
            Assert.Equal(10, client.MediaGroupUploads[0].FilePaths.Count);
            Assert.Equal(2, client.MediaGroupUploads[1].FilePaths.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessQueueAsync_DoesNotBatchAcrossTargetChats()
    {
        var dirA = CreateTempDir();
        var dirB = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dirA, "a.txt"), "a"u8.ToArray());
            File.WriteAllBytes(Path.Combine(dirB, "b.txt"), "b"u8.ToArray());

            var client = new FakeTelegramClient();
            var manager = new UploadManager(client, new FakeStateRepository());
            var queue = manager.BuildQueue(
            [
                new UploadJobOptions(dirA, "@chan_a", Recursive: false),
                new UploadJobOptions(dirB, "@chan_b", Recursive: false),
            ]);

            await manager.ProcessQueueAsync(queue);

            Assert.Equal(2, client.MediaGroupUploads.Count);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessQueueAsync_SkipsAlreadyUploadedFile()
    {
        var dir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(dir, "a.txt");
            File.WriteAllBytes(filePath, "a"u8.ToArray());

            var stateRepository = new FakeStateRepository();
            var dedupKey = ContentHash.ComputeUploadDedupKey(filePath);
            await stateRepository.MarkFileUploadedAsync("@chan", dedupKey, filePath);

            var client = new FakeTelegramClient();
            var manager = new UploadManager(client, stateRepository);
            var queue = manager.BuildQueue([new UploadJobOptions(dir, "@chan", Recursive: false)]);

            await manager.ProcessQueueAsync(queue);

            Assert.Empty(client.MediaGroupUploads);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessQueueAsync_ReportsErrorForWholeBatchAndContinues()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.txt"), "a"u8.ToArray());

            var client = new FakeTelegramClient { UploadMediaGroupException = new InvalidOperationException("boom") };
            var errors = new List<(string, string)>();
            var reporter = new RecordingUploadReporter();
            var manager = new UploadManager(client, new FakeStateRepository(), reporter);
            var queue = manager.BuildQueue([new UploadJobOptions(dir, "@chan", Recursive: false)]);

            await manager.ProcessQueueAsync(queue);

            Assert.Single(reporter.Errors);
            Assert.Equal("a.txt", reporter.Errors[0].FileName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class RecordingUploadReporter : IUploadProgressReporter
    {
        public List<(string FileName, string Error)> Errors { get; } = [];
        public void OnFileProgress(UploadFileProgress progress) { }
        public void OnFileComplete(string fileName) { }
        public void OnFileError(string fileName, string error) => Errors.Add((fileName, error));
        public void OnFileSkipped(string fileName) { }
        public void OnQueueProgress(UploadQueueProgress progress) { }
    }
}
