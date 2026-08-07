using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;
using TelegramMediaGrabber.Application.Telegram;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode export-links --target &lt;chat&gt;</c>: scans a chat's message
/// history and writes every link Telegram itself recognized (its own
/// entity parsing — bare auto-detected URLs and markdown-style
/// <c>[text](url)</c> links alike, see <see cref="TelegramMessage.Links"/>)
/// to a JSON file, one entry per link. Purely a read — never joins, never
/// touches state.db, never downloads media.
/// </summary>
public sealed class ExportLinksCommand(
    ITelegramClient client, IAnsiConsole console, string target, int? maxMessages, string exportsDir)
{
    // One row per link, not per message: a single post commonly lists
    // several distinct titles each with their own link right below it
    // (e.g. "Title A\nlinkA\n\nTitle B\nlinkB") -- bundling those under
    // one message object as an array of bare URLs was the exact bug
    // reported against the first version of this command, since it threw
    // away which title belonged to which link.
    private sealed record LinkExport(int MessageId, DateTimeOffset Date, long? SenderId, string? Label, string Link, string? MessageText);

    // Relaxed encoder: this is a local file for the user to read/grep, not
    // web output, so there's no reason for the default encoder to escape
    // '+' (Telegram invite links are full of them) or other characters
    // that are only unsafe in a browser context.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        console.MarkupLine($"Resolving [bold]{Markup.Escape(target)}[/]...");
        var entity = await ResolveAsync(cancellationToken);
        console.MarkupLine($"Resolved to [green]{Markup.Escape(entity.DisplayName)}[/] (chat ID {entity.Id}). Scanning messages...");

        var found = new List<LinkExport>();
        var scanned = 0;
        var messagesWithLinks = 0;
        await foreach (var message in client.IterMessagesAsync(entity, minId: 0, limit: maxMessages, cancellationToken))
        {
            scanned++;
            if (scanned % 500 == 0)
            {
                console.MarkupLine($"  ...scanned {scanned}, found {found.Count} link(s) so far");
            }

            if (message.Links is not { Count: > 0 } links)
            {
                continue;
            }

            messagesWithLinks++;
            foreach (var link in links)
            {
                found.Add(new LinkExport(message.Id, message.Date, message.SenderId, link.Label, link.Url, message.Text));
            }
        }

        // Oldest-first in the file -- matches reading order, easier to
        // skim than Telegram's own newest-first iteration order.
        found.Reverse();

        Directory.CreateDirectory(exportsDir);
        var safeName = string.Concat(entity.DisplayName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var fileName = $"links_{safeName}_{entity.Id}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.json";
        var path = Path.Combine(exportsDir, fileName);

        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, found, JsonOptions, cancellationToken);
        }

        console.MarkupLine(
            $"[bold]Scanned {scanned}[/] message(s); [green]{messagesWithLinks}[/] contained a link, " +
            $"[green]{found.Count}[/] link(s) total.");
        console.MarkupLine($"Written to [bold]{Markup.Escape(path)}[/]");
    }

    /// <summary>
    /// <paramref name="target"/> is documented as accepting a chat ID,
    /// "@username", or invite link (same as everywhere else this app
    /// resolves a chat) -- but a plain chat *title* (e.g. copy-pasted
    /// straight from the Telegram UI, no "@") isn't any of those, and
    /// <see cref="ITelegramClient.ResolveEntityAsync"/> fails on it
    /// (<c>USERNAME_INVALID</c>). Falls back to an exact title match
    /// against this account's own joined-chat list -- same recovery
    /// <see cref="ResolveIdsCommand"/> uses for a renamed channel -- so a
    /// title copy-pasted as-is still works without the user having to go
    /// find the chat's real ID/username by hand.
    /// </summary>
    private async Task<TelegramEntity> ResolveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.ResolveEntityAsync(target, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            var byTitle = await client.TryResolveByTitleAsync(target, cancellationToken);
            if (byTitle is not null)
            {
                return byTitle;
            }

            throw;
        }
    }
}
