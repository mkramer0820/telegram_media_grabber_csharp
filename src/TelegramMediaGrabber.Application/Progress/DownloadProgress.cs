namespace TelegramMediaGrabber.Application.Progress;

public sealed record FileProgress(string ChatName, int MessageId, string FileName, long BytesDone, long BytesTotal);

public sealed record ChannelProgress(string ChatName, int MessagesScanned, int FilesDownloaded, bool Done);

/// <summary>
/// Callback surface the UI layer implements to observe download progress.
/// Methods must return quickly. Kept as an interface (not a direct
/// Spectre.Console dependency) so Application never references the UI
/// library — AGENTS.md §1.3 / CSHARP_PORT_GUIDE.md §6.
/// </summary>
public interface IDownloadProgressReporter
{
    void OnFileProgress(FileProgress progress);
    void OnFileComplete(string chatName, int messageId, string finalPath);
    void OnFileError(string chatName, int messageId, string error);
    void OnChannelProgress(ChannelProgress progress);
    void OnFloodWait(double seconds);
}

/// <summary>No-op reporter used when the caller doesn't supply one.</summary>
public sealed class NullDownloadProgressReporter : IDownloadProgressReporter
{
    public static readonly NullDownloadProgressReporter Instance = new();
    public void OnFileProgress(FileProgress progress) { }
    public void OnFileComplete(string chatName, int messageId, string finalPath) { }
    public void OnFileError(string chatName, int messageId, string error) { }
    public void OnChannelProgress(ChannelProgress progress) { }
    public void OnFloodWait(double seconds) { }
}
