using Spectre.Console;
using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Downloading;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Application.Uploading;
using TelegramMediaGrabber.Cli.Ui;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// Default mode (no <c>--mode</c> given, or explicit <c>--mode run</c>):
/// does everything <c>config/channels.yaml</c> declares, in one
/// continuous process --
/// <list type="number">
/// <item>an initial catch-up download pass over every configured channel
/// (same as <c>--mode download</c>), so watching starts from a known,
/// consistent point;</item>
/// <item>then, concurrently, for as long as the process runs: real-time
/// downloading of new channel messages as Telegram pushes them (same as
/// <c>--mode watch</c>), and, if any <c>upload_jobs</c> are configured, a
/// periodic re-scan/upload pass paced by
/// <see cref="ChannelsOptions.UploadIntervalSeconds"/> (same
/// batching/pacing as a manual <c>--mode upload</c> run -- a large backlog
/// found in one scan is still sent in paced, size-limited media-group
/// batches, not all at once).</item>
/// </list>
/// The single-purpose <c>--mode download</c>/<c>upload</c>/<c>watch</c>/
/// <c>verify</c>/<c>reprocess</c> commands stay available for manual
/// override/recovery use (e.g. forcing an extra catch-up scan, or
/// re-verifying tags) -- this is the "just run it" default behavior
/// driven entirely by config.
/// </summary>
public sealed class RunCommand(
    ITelegramClient client,
    IStateRepository stateRepository,
    IAudiobookTagger tagger,
    ChannelsOptions options,
    string audiobooksDestDir,
    IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var audiobookProcessor = new AudiobookProcessingService(tagger);
        var parsingService = new ChapterParsingService();
        var manager = new DownloadManager(
            client, stateRepository, audiobookProcessor, parsingService,
            options.DownloadRoot, options.MaxConcurrentDownloads, reporter: null, audiobooksDestDir: audiobooksDestDir);

        console.MarkupLine($"Catching up on [bold]{options.Channels.Count}[/] channel(s)...");
        await manager.RunAsync(options.Channels, cancellationToken);

        var uploadNote = options.UploadJobs.Count > 0
            ? $" Re-scanning [bold]{options.UploadJobs.Count}[/] upload job(s) every {options.UploadIntervalSeconds}s."
            : string.Empty;
        console.MarkupLine($"[green]Caught up.[/] Watching for new messages.{uploadNote} Press Ctrl+C to stop.");

        var watchTask = manager.WatchAsync(options.Channels, cancellationToken);
        var uploadTask = options.UploadJobs.Count > 0
            ? RunUploadLoopAsync(cancellationToken)
            : Task.CompletedTask;

        await Task.WhenAll(watchTask, uploadTask);
    }

    /// <summary>
    /// Repeats a paced upload scan (identical to a manual <c>--mode
    /// upload</c> pass) every <see cref="ChannelsOptions.UploadIntervalSeconds"/>,
    /// starting with an immediate first pass rather than waiting out the
    /// first interval. Only runs at all if <see cref="ChannelsOptions.UploadJobs"/>
    /// is non-empty (checked by the caller).
    /// </summary>
    private async Task RunUploadLoopAsync(CancellationToken cancellationToken)
    {
        var manager = new UploadManager(client, stateRepository);
        while (!cancellationToken.IsCancellationRequested)
        {
            var queue = manager.BuildQueue(options.UploadJobs);
            if (queue.Count > 0)
            {
                // queue.Count is every file currently sitting in the
                // configured folders, not just new ones -- ProcessQueueAsync
                // dedup-skips anything already marked uploaded, so this is
                // an upper bound on what actually gets sent this pass.
                console.MarkupLine($"[dim]Upload scan: {queue.Count} file(s) found, checking for new ones...[/]");
                await manager.ProcessQueueAsync(queue, cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(options.UploadIntervalSeconds), cancellationToken);
        }
    }
}
