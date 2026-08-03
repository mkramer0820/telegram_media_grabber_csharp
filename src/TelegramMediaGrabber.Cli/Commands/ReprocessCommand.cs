using Spectre.Console;
using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Application.State;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode reprocess</c>: fully offline. Tags/relocates audiobook_mode
/// files stuck in staging and fixes their state record where one exists.
/// Mirrors <c>src/main.py::_run_reprocess</c>.
/// </summary>
public sealed class ReprocessCommand(IStateRepository stateRepository, IAudiobookTagger tagger, ChannelsOptions options, string audiobooksDestDir, IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var channels = options.Channels.Where(c => c.AudiobookMode && c.Metadata is not null).ToList();
        if (channels.Count == 0)
        {
            console.MarkupLine("[yellow]No audiobook_mode channels configured; nothing to reprocess.[/]");
            return;
        }

        var service = new ReprocessService(stateRepository, new AudiobookProcessingService(tagger), new ChapterParsingService());
        var report = new Progress<string>(line => console.MarkupLine(Markup.Escape(line)));

        var summary = await service.RunAsync(channels, options.DownloadRoot, audiobooksDestDir, report, cancellationToken);

        console.MarkupLine(
            $"[bold]Reprocessed {summary.Processed}[/] file(s) ({summary.ProcessedWithoutRecord} with no prior state record), {summary.Errors} error(s).");
    }
}
