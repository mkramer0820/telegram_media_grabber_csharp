using Spectre.Console;
using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode verify</c>: online, re-checks already-tagged audiobook
/// episode numbers against Telegram directly. Mirrors
/// <c>src/main.py::_run_verify</c>.
/// </summary>
public sealed class VerifyCommand(
    ITelegramClient client, IStateRepository stateRepository, IAudiobookTagger tagger,
    ChannelsOptions options, string audiobooksDestDir, IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var channels = options.Channels.Where(c => c.AudiobookMode && c.Metadata is not null).ToList();
        if (channels.Count == 0)
        {
            console.MarkupLine("[yellow]No audiobook_mode channels configured; nothing to verify.[/]");
            return;
        }

        var service = new VerifyService(client, stateRepository, new AudiobookProcessingService(tagger));
        var report = new Progress<string>(line => console.MarkupLine(Markup.Escape(line)));

        var totals = new VerifySummary(0, 0, 0);
        foreach (var channel in channels)
        {
            console.MarkupLine($"Verifying [bold]{Markup.Escape(channel.Name)}[/]...");
            totals += await service.RunChannelAsync(channel, audiobooksDestDir, report, cancellationToken);
        }

        console.MarkupLine($"[bold]Checked {totals.Checked}[/] file(s); {totals.Corrected} corrected, {totals.Errors} error(s).");
    }
}
