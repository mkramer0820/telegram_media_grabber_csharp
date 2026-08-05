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
/// config file.
/// </summary>
/// <remarks>
/// If a configured username/link fails to resolve (e.g. the channel
/// renamed itself, causing <c>USERNAME_NOT_OCCUPIED</c>) but this account
/// is still a member, falls back to an exact title match against the
/// account's own joined-chat list (<see cref="ITelegramClient.TryResolveByTitleAsync"/>)
/// using the channel's configured <c>metadata.novel_title</c> — never
/// guesses at a new username, never joins anything.
/// </remarks>
/// <param name="writeBack">
/// If true (<c>--write</c>), rewrites <paramref name="channelsConfigPath"/>
/// in place: every channel whose resolved permanent chat ID differs from
/// what's currently configured gets its <c>id:</c> line updated to that
/// numeric ID, with the original configured value preserved in a trailing
/// comment for traceability. A targeted per-line text replace, not a
/// full YAML round-trip — deliberately, so it can never destroy the rest
/// of the file's comments/formatting. Skips (and reports) any line it
/// can't find an unambiguous match for, rather than guessing.
/// </param>
public sealed class ResolveIdsCommand(
    ITelegramClient client,
    IStateRepository stateRepository,
    ChannelsOptions options,
    IAnsiConsole console,
    bool writeBack = false,
    string? channelsConfigPath = null)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var table = new Table()
            .AddColumn("Name")
            .AddColumn("Configured as")
            .AddColumn("Chat ID")
            .AddColumn("Title")
            .AddColumn("Username")
            .AddColumn("Kind")
            .AddColumn("Note");

        var pendingUpdates = new List<(string ConfiguredValue, string NewChatId)>();

        foreach (var channel in options.Channels)
        {
            await ResolveOneAsync(table, channel.Name, channel.Id, channel.Metadata?.NovelTitle, pendingUpdates, cancellationToken);
        }

        foreach (var job in options.UploadJobs)
        {
            await ResolveOneAsync(table, $"upload_jobs: {job.SourceDir}", job.TargetChat, novelTitle: null, pendingUpdates, cancellationToken);
        }

        console.Write(table);

        if (!writeBack)
        {
            console.MarkupLine(
                "[dim]Cached in the state database for future reference. Nothing was written to " +
                "config/channels.yaml — re-run with --write to pin these permanent Chat IDs into the config " +
                "(original value kept in a comment), or paste one in by hand.[/]");
            return;
        }

        if (pendingUpdates.Count == 0)
        {
            console.MarkupLine("[dim]--write: nothing to update — every entry already matches its resolved Chat ID.[/]");
            return;
        }

        if (channelsConfigPath is null || !File.Exists(channelsConfigPath))
        {
            console.MarkupLine($"[red]--write: config file not found at {Markup.Escape(channelsConfigPath ?? "(unset)")}, nothing written.[/]");
            return;
        }

        var updated = RewriteConfig(channelsConfigPath, pendingUpdates);
        console.MarkupLine($"[green]--write: pinned {updated} of {pendingUpdates.Count} entr{(pendingUpdates.Count == 1 ? "y" : "ies")} " +
            $"in {Markup.Escape(channelsConfigPath)} to their permanent Chat ID (original value kept in a comment).[/]");
        if (updated < pendingUpdates.Count)
        {
            console.MarkupLine("[yellow]Some entries couldn't be uniquely matched in the file text and were left alone — update those by hand.[/]");
        }
    }

    private async Task ResolveOneAsync(
        Table table, string name, string configuredId, string? novelTitle,
        List<(string ConfiguredValue, string NewChatId)> pendingUpdates, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await client.ResolveEntityAsync(configuredId, cancellationToken);
            await stateRepository.CacheResolvedEntityAsync(configuredId, entity, cancellationToken);
            AddResolvedRow(table, name, configuredId, entity, note: "-", pendingUpdates);
        }
        catch (Exception ex)
        {
            if (novelTitle is not null)
            {
                var byTitle = await client.TryResolveByTitleAsync(novelTitle, cancellationToken);
                if (byTitle is not null)
                {
                    await stateRepository.CacheResolvedEntityAsync(configuredId, byTitle, cancellationToken);
                    AddResolvedRow(table, name, configuredId, byTitle, note: "recovered by title match (renamed?)", pendingUpdates);
                    return;
                }
            }

            table.AddRow(
                Markup.Escape(name), Markup.Escape(configuredId),
                $"[red]Failed: {Markup.Escape(ex.Message)}[/]", "-", "-", "-",
                novelTitle is null ? "-" : "[red]title match also failed — not in joined chats?[/]");
        }
    }

    private void AddResolvedRow(
        Table table, string name, string configuredId, TelegramEntity entity, string note,
        List<(string ConfiguredValue, string NewChatId)> pendingUpdates)
    {
        var chatIdText = entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var alreadyPinned = string.Equals(configuredId, chatIdText, StringComparison.Ordinal);

        table.AddRow(
            Markup.Escape(name),
            Markup.Escape(configuredId),
            $"[green]{entity.Id}[/]",
            Markup.Escape(entity.DisplayName),
            Markup.Escape(entity.Username ?? "-"),
            Markup.Escape(entity.Kind ?? "-"),
            Markup.Escape(note));

        if (!alreadyPinned)
        {
            pendingUpdates.Add((configuredId, chatIdText));
        }
    }

    /// <summary>
    /// Targeted per-line replace of <c>id: "&lt;configured value&gt;"</c> with the
    /// resolved numeric Chat ID, keeping the original value in a trailing
    /// comment. Only touches lines that match exactly once — a value that's
    /// ambiguous or already edited is left for a human rather than guessed at.
    /// </summary>
    private static int RewriteConfig(string path, List<(string ConfiguredValue, string NewChatId)> updates)
    {
        var text = File.ReadAllText(path);
        var updated = 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        foreach (var (configuredValue, newChatId) in updates)
        {
            var target = $"id: \"{configuredValue}\"";
            var firstIndex = text.IndexOf(target, StringComparison.Ordinal);
            if (firstIndex < 0 || text.IndexOf(target, firstIndex + 1, StringComparison.Ordinal) >= 0)
            {
                // Not found, or found more than once -- ambiguous, skip rather than guess.
                continue;
            }

            var replacement = $"id: \"{newChatId}\"  # was \"{configuredValue}\" (renamed) -- pinned to permanent chat ID by --write on {today}";
            text = string.Concat(text.AsSpan(0, firstIndex), replacement, text.AsSpan(firstIndex + target.Length));
            updated++;
        }

        File.WriteAllText(path, text);
        return updated;
    }
}
