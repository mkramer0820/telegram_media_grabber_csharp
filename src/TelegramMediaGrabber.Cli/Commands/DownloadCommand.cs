using Spectre.Console;
using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Downloading;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Cli.Ui;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode download</c>: scans every configured channel and downloads
/// new media. Mirrors <c>src/main.py::_run_download</c> in the Python
/// predecessor.
/// </summary>
public sealed class DownloadCommand(
    ITelegramClient client,
    IStateRepository stateRepository,
    IAudiobookTagger tagger,
    ChannelsOptions options,
    string audiobooksDestDir,
    IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        console.MarkupLine($"[bold]{options.Channels.Count}[/] channel(s) configured:");
        foreach (var channel in options.Channels)
        {
            console.MarkupLine($"  - {Markup.Escape(channel.Name)}");
        }

        var audiobookProcessor = new AudiobookProcessingService(tagger);
        var parsingService = new ChapterParsingService();

        await console.Live(new Markup("Starting download...")).StartAsync(async ctx =>
        {
            var dashboard = new DownloadDashboard(ctx);
            dashboard.SeedChannels(options.Channels.Select(c => c.Name));
            var manager = new DownloadManager(
                client, stateRepository, audiobookProcessor, parsingService,
                options.DownloadRoot, options.MaxConcurrentDownloads, dashboard, audiobooksDestDir);

            await manager.RunAsync(options.Channels, cancellationToken);
        });
    }
}
