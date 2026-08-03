namespace TelegramMediaGrabber.Application.Progress;

public sealed record UploadFileProgress(string FileName, long BytesDone, long BytesTotal);

public sealed record UploadQueueProgress(int FilesTotal, int FilesUploaded, int FilesSkipped, bool Done);

public interface IUploadProgressReporter
{
    void OnFileProgress(UploadFileProgress progress);
    void OnFileComplete(string fileName);
    void OnFileError(string fileName, string error);
    void OnFileSkipped(string fileName);
    void OnQueueProgress(UploadQueueProgress progress);
}

public sealed class NullUploadProgressReporter : IUploadProgressReporter
{
    public static readonly NullUploadProgressReporter Instance = new();
    public void OnFileProgress(UploadFileProgress progress) { }
    public void OnFileComplete(string fileName) { }
    public void OnFileError(string fileName, string error) { }
    public void OnFileSkipped(string fileName) { }
    public void OnQueueProgress(UploadQueueProgress progress) { }
}
