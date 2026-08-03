using Spectre.Console;
using Spectre.Console.Rendering;
using TelegramMediaGrabber.Application.Progress;

namespace TelegramMediaGrabber.Cli.Ui;

/// <summary>Live Spectre.Console dashboard for upload mode. See <see cref="DownloadDashboard"/> for the dependency-direction rationale.</summary>
public sealed class UploadDashboard(LiveDisplayContext context) : IUploadProgressReporter
{
    private string _queueStatus = "";
    private string _statusLine = "";

    private IRenderable Render()
    {
        var rows = new List<IRenderable>();
        if (_queueStatus.Length > 0)
        {
            rows.Add(new Panel(_queueStatus) { Header = new PanelHeader("Queue"), Border = BoxBorder.Rounded });
        }

        if (_statusLine.Length > 0)
        {
            rows.Add(new Panel(_statusLine) { Border = BoxBorder.Rounded });
        }

        return rows.Count > 0 ? new Rows(rows) : new Markup("Starting upload...");
    }

    private void Refresh()
    {
        context.UpdateTarget(Render());
        context.Refresh();
    }

    public void OnFileProgress(UploadFileProgress progress)
    {
        var percent = progress.BytesTotal > 0 ? 100.0 * progress.BytesDone / progress.BytesTotal : 0;
        _statusLine = $"[cyan]Uploading:[/] {Markup.Escape(progress.FileName)} ({percent:F0}%)";
        Refresh();
    }

    public void OnFileComplete(string fileName)
    {
        _statusLine = $"[green]Uploaded:[/] {Markup.Escape(fileName)}";
        Refresh();
    }

    public void OnFileError(string fileName, string error)
    {
        _statusLine = $"[red]Failed[/] ({Markup.Escape(fileName)}): {Markup.Escape(error)}";
        Refresh();
    }

    public void OnFileSkipped(string fileName)
    {
        _statusLine = $"[dim]Skipped (already uploaded):[/] {Markup.Escape(fileName)}";
        Refresh();
    }

    public void OnQueueProgress(UploadQueueProgress progress)
    {
        var status = progress.Done ? "done" : "uploading";
        _queueStatus = $"{progress.FilesUploaded} uploaded, {progress.FilesSkipped} skipped, {progress.FilesTotal} total ({status})";
        Refresh();
    }
}
