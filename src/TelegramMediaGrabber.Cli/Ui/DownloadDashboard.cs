using Spectre.Console;
using Spectre.Console.Rendering;
using TelegramMediaGrabber.Application.Progress;

namespace TelegramMediaGrabber.Cli.Ui;

/// <summary>
/// Live Spectre.Console dashboard for download mode. Implements
/// <see cref="IDownloadProgressReporter"/> so it can be handed directly to
/// <c>DownloadManager</c> without the Application layer ever referencing
/// Spectre.Console (AGENTS.md §1.3 / §4.1 dependency direction).
/// </summary>
public sealed class DownloadDashboard(LiveDisplayContext context) : IDownloadProgressReporter
{
    private readonly Dictionary<string, ChannelProgress> _channelRows = [];
    private string _statusLine = "";

    private IRenderable Render()
    {
        var table = new Table().Title("Channels").Expand();
        table.AddColumn("Channel");
        table.AddColumn(new TableColumn("Scanned").RightAligned());
        table.AddColumn(new TableColumn("Downloaded").RightAligned());
        table.AddColumn(new TableColumn("Status").RightAligned());

        foreach (var progress in _channelRows.Values)
        {
            var status = progress.Done ? "[green]done[/]" : "[yellow]scanning[/]";
            table.AddRow(
                Markup.Escape(progress.ChatName),
                progress.MessagesScanned.ToString(),
                progress.FilesDownloaded.ToString(),
                status);
        }

        var rows = new List<IRenderable> { table };
        if (_statusLine.Length > 0)
        {
            rows.Add(new Panel(_statusLine) { Border = BoxBorder.Rounded });
        }

        return new Rows(rows);
    }

    private void Refresh()
    {
        context.UpdateTarget(Render());
        context.Refresh();
    }

    public void OnFileProgress(FileProgress progress)
    {
        var percent = progress.BytesTotal > 0 ? 100.0 * progress.BytesDone / progress.BytesTotal : 0;
        _statusLine = $"[cyan]{Markup.Escape(progress.ChatName)}[/] {Markup.Escape(progress.FileName)} ({percent:F0}%)";
        Refresh();
    }

    public void OnFileComplete(string chatName, int messageId, string finalPath)
    {
        _statusLine = $"[green]Saved:[/] {Markup.Escape(Path.GetFileName(finalPath))}";
        Refresh();
    }

    public void OnFileError(string chatName, int messageId, string error)
    {
        _statusLine = $"[red]Failed[/] (chat={Markup.Escape(chatName)} message={messageId}): {Markup.Escape(error)}";
        Refresh();
    }

    public void OnChannelProgress(ChannelProgress progress)
    {
        _channelRows[progress.ChatName] = progress;
        Refresh();
    }

    public void OnFloodWait(double seconds)
    {
        _statusLine = $"[yellow]FloodWait: pausing {seconds:F1}s[/]";
        Refresh();
    }
}
