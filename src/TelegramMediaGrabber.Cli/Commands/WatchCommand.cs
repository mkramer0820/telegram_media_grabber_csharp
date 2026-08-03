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
/// <c>--mode watch</c>: stays connected and downloads new media from
/// configured channels as Telegram pushes it (no polling). Does not scan
/// channel backlog — run <c>--mode download</c> first (and again after any
/// gap, e.g. this process having been down) to catch up.
/// </summary>
public sealed class WatchCommand(
    ITelegramClient client,
    IStateRepository stateRepository,
    IAudiobookTagger tagger,
    ChannelsOptions options,
    string audiobooksDestDir,
    IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        console.MarkupLine(
            $"Watching [bold]{options.Channels.Count}[/] channel(s) for new messages. " +
            "This only catches new posts from now on — run [bold]--mode download[/] first " +
            "to pick up anything already posted. Press Ctrl+C to stop.");

        var audiobookProcessor = new AudiobookProcessingService(tagger);
        var parsingService = new ChapterParsingService();

        await console.Live(new Markup("Waiting for new messages...")).StartAsync(async ctx =>
        {
            var dashboard = new DownloadDashboard(ctx);
            var manager = new DownloadManager(
                client, stateRepository, audiobookProcessor, parsingService,
                options.DownloadRoot, options.MaxConcurrentDownloads, dashboard, audiobooksDestDir);

            await manager.WatchAsync(options.Channels, cancellationToken);
        });
    }
}
