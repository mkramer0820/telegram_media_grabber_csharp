using Spectre.Console;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.State;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode resolve-ids</c>: resolves every configured channel/upload-job
/// chat (however it's written — bare/@-prefixed username, t.me URL,
/// invite link, or already-numeric ID) and records whatever Telegram
/// reports about it (permanent numeric ID, title, username, kind) in the
/// state database via <see cref="IStateRepository.CacheResolvedEntityAsync"/>,
/// so there's a durable, "hunt for it later" record independent of the
/// config file. Prints a report; does not rewrite <c>config/channels.yaml</c>
/// itself (a naive rewrite risks destroying comments/formatting in a live
/// config) — copy a numeric ID in by hand if you want that entry to stop
/// depending on a username/link that could change or expire later.
/// </summary>
public sealed class ResolveIdsCommand(ITelegramClient client, IStateRepository stateRepository, ChannelsOptions options, IAnsiConsole console)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var table = new Table()
            .AddColumn("Name")
            .AddColumn("Configured as")
            .AddColumn("Chat ID")
            .AddColumn("Title")
            .AddColumn("Username")
            .AddColumn("Kind");

        foreach (var channel in options.Channels)
        {
            await ResolveOneAsync(table, channel.Name, channel.Id, cancellationToken);
        }

        foreach (var job in options.UploadJobs)
        {
            await ResolveOneAsync(table, $"upload_jobs: {job.SourceDir}", job.TargetChat, cancellationToken);
        }

        console.Write(table);
        console.MarkupLine(
            "[dim]Cached in the state database for future reference. Nothing was written to " +
            "config/channels.yaml — paste a Chat ID in by hand if you want that entry to stop depending " +
            "on a username/link that could change or expire later.[/]");
    }

    private async Task ResolveOneAsync(Table table, string name, string configuredId, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await client.ResolveEntityAsync(configuredId, cancellationToken);
            await stateRepository.CacheResolvedEntityAsync(configuredId, entity, cancellationToken);

            table.AddRow(
                Markup.Escape(name),
                Markup.Escape(configuredId),
                $"[green]{entity.Id}[/]",
                Markup.Escape(entity.DisplayName),
                Markup.Escape(entity.Username ?? "-"),
                Markup.Escape(entity.Kind ?? "-"));
        }
        catch (Exception ex)
        {
            table.AddRow(
                Markup.Escape(name), Markup.Escape(configuredId),
                $"[red]Failed: {Markup.Escape(ex.Message)}[/]", "-", "-", "-");
        }
    }
}
