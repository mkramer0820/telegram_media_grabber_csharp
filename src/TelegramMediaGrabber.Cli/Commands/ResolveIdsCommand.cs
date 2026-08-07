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
        // A Spectre Table here previously mangled exactly the fields you
        // need to tell two similarly-named/similarly-purposed channels
        // apart -- cramming 5-7 columns into a normal terminal width wraps
        // "Name"/"Title" mid-word (e.g. "shadow_sl\nave_audio\nbook"), or
        // (if those columns are forced NoWrap instead) starves Chat
        // ID/Link down to 1-character-wide columns instead. A one-line-
        // per-field block per channel has no column-width fight to lose:
        // every field is always shown in full, at any terminal width.
        var pendingUpdates = new List<(string ConfiguredValue, string NewChatId, string Title)>();

        foreach (var channel in options.Channels)
        {
            await ResolveOneAsync(channel.Name, channel.Id, channel.Metadata?.NovelTitle, pendingUpdates, cancellationToken);
        }

        foreach (var job in options.UploadJobs)
        {
            await ResolveOneAsync($"upload_jobs: {job.SourceDir}", job.TargetChat, novelTitle: null, pendingUpdates, cancellationToken);
        }

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
        string name, string configuredId, string? novelTitle,
        List<(string ConfiguredValue, string NewChatId, string Title)> pendingUpdates, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await client.ResolveEntityAsync(configuredId, cancellationToken);
            await stateRepository.CacheResolvedEntityAsync(configuredId, entity, cancellationToken);
            PrintResolved(name, configuredId, entity, note: null, pendingUpdates);
        }
        catch (Exception ex)
        {
            if (novelTitle is not null)
            {
                var byTitle = await client.TryResolveByTitleAsync(novelTitle, cancellationToken);
                if (byTitle is not null)
                {
                    await stateRepository.CacheResolvedEntityAsync(configuredId, byTitle, cancellationToken);
                    PrintResolved(name, configuredId, byTitle, note: "recovered by title match (renamed?)", pendingUpdates);
                    return;
                }
            }

            console.MarkupLine($"[bold]{Markup.Escape(name)}[/]");
            console.MarkupLine($"  Configured as: {Markup.Escape(configuredId)}");
            console.MarkupLine($"  [red]Failed: {Markup.Escape(ex.Message)}[/]" +
                (novelTitle is null ? "" : " [red](title match also failed, not in joined chats?)[/]"));
            console.WriteLine();
        }
    }

    private void PrintResolved(
        string name, string configuredId, TelegramEntity entity, string? note,
        List<(string ConfiguredValue, string NewChatId, string Title)> pendingUpdates)
    {
        var chatIdText = entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var alreadyPinned = string.Equals(configuredId, chatIdText, StringComparison.Ordinal);

        // Prefer a real t.me/username link when the channel has a public
        // one; most of these are private invite-link channels though, so
        // fall back to whatever was configured (usually already the
        // invite link itself) rather than showing a bare "-".
        var link = entity.Username is { } username ? $"https://t.me/{username}" : configuredId;

        console.MarkupLine($"[bold]{Markup.Escape(name)}[/]");
        console.MarkupLine($"  Title:   {Markup.Escape(entity.DisplayName)}");
        console.MarkupLine($"  Chat ID: [green]{entity.Id}[/]{(alreadyPinned ? " [dim](already pinned)[/]" : "")}");
        console.MarkupLine($"  Link:    {Markup.Escape(link)}");
        if (note is not null)
        {
            console.MarkupLine($"  Note:    [yellow]{Markup.Escape(note)}[/]");
        }

        console.WriteLine();

        if (!alreadyPinned)
        {
            pendingUpdates.Add((configuredId, chatIdText, entity.DisplayName));
        }
    }

    /// <summary>
    /// Targeted per-line replace of <c>id: "&lt;configured value&gt;"</c> with the
    /// resolved numeric Chat ID, keeping the original value and the
    /// resolved title in a trailing comment -- the title only ever comes
    /// from <paramref name="updates"/>, i.e. only for an entry that just
    /// got freshly, successfully re-resolved this run ("the version
    /// that's good") -- a failed resolve never reaches this method at
    /// all, so a bad/unconfirmed title can never get written into the
    /// config. Only touches lines that match exactly once — a value
    /// that's ambiguous or already edited is left for a human rather than
    /// guessed at.
    /// </summary>
    private static int RewriteConfig(string path, List<(string ConfiguredValue, string NewChatId, string Title)> updates)
    {
        var text = File.ReadAllText(path);
        var updated = 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        foreach (var (configuredValue, newChatId, title) in updates)
        {
            var target = $"id: \"{configuredValue}\"";
            var firstIndex = text.IndexOf(target, StringComparison.Ordinal);
            if (firstIndex < 0 || text.IndexOf(target, firstIndex + 1, StringComparison.Ordinal) >= 0)
            {
                // Not found, or found more than once -- ambiguous, skip rather than guess.
                continue;
            }

            var safeTitle = title.Replace("\"", "'");
            var replacement = $"id: \"{newChatId}\"  # \"{safeTitle}\" -- was \"{configuredValue}\" (renamed) -- pinned to permanent chat ID by --write on {today}";
            text = string.Concat(text.AsSpan(0, firstIndex), replacement, text.AsSpan(firstIndex + target.Length));
            updated++;
        }

        File.WriteAllText(path, text);
        return updated;
    }
}
