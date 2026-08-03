using Spectre.Console;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Application.Uploading;
using TelegramMediaGrabber.Cli.Ui;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode upload</c>: runs every configured upload job. Mirrors
/// <c>src/main.py::_run_upload</c>.
/// </summary>
public sealed class UploadCommand(ITelegramClient client, IStateRepository stateRepository, ChannelsOptions options, IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (options.UploadJobs.Count == 0)
        {
            console.MarkupLine("[yellow]No upload_jobs configured in config/channels.yaml; upload mode has nothing to send.[/]");
            return;
        }

        console.MarkupLine($"Running [bold]{options.UploadJobs.Count}[/] upload job(s).");

        await console.Live(new Markup("Starting upload...")).StartAsync(async ctx =>
        {
            var dashboard = new UploadDashboard(ctx);
            var manager = new UploadManager(client, stateRepository, dashboard);
            var queue = manager.BuildQueue(options.UploadJobs);
            await manager.ProcessQueueAsync(queue, cancellationToken);
        });
    }
}
