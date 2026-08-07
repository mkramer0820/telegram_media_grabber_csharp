using System.Text;
using Spectre.Console;
using TelegramMediaGrabber.Application.Telegram;
using TelegramMediaGrabber.Infrastructure.Configuration;

namespace TelegramMediaGrabber.Cli.Commands;

/// <summary>
/// <c>--mode links-to-jobs --target &lt;chat&gt;</c>: scans a chat for links
/// (same extraction as <see cref="ExportLinksCommand"/> — one entry per
/// link, each with its own best-effort <see cref="LinkEntry.Label"/>),
/// resolves each distinct link directly to get its real Telegram channel
/// title (falling back to the message-text label, then the URL itself,
/// if a link can't be resolved), creates an empty
/// <c>uploads/&lt;slug&gt;/</c> folder for each, and writes a
/// ready-to-paste <c>upload_jobs:</c> YAML block pairing each folder with
/// its <c>target_chat</c>.
/// </summary>
/// <remarks>
/// Deliberately never touches <c>channels.yaml</c> itself — writes a
/// separate <c>.yaml</c> file under <paramref name="exportsDir"/> instead,
/// so the user can review and paste in only the entries they actually
/// want, same as a manual edit would. Directory creation is the only
/// filesystem side effect against the real project layout, and it's
/// purely additive (mkdir, never touches existing folders/files).
/// </remarks>
public sealed class LinksToJobsCommand(
    ITelegramClient client, IAnsiConsole console, string target, int? maxMessages, string uploadsDir, string exportsDir)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        console.MarkupLine($"Resolving [bold]{Markup.Escape(target)}[/]...");
        var entity = await ResolveAsync(cancellationToken);
        console.MarkupLine($"Resolved to [green]{Markup.Escape(entity.DisplayName)}[/] (chat ID {entity.Id}). Scanning messages...");

        // Dedup by URL -- the same invite link occasionally gets reposted
        // (a reminder post with just the bare link, a repost after a typo
        // fix, etc.). Keep the best label seen across every occurrence,
        // not just the first one encountered: IterMessagesAsync yields
        // newest-first, so a label-less reminder repost would otherwise
        // "win" the dedup over an earlier post that had the real title
        // right above its link.
        var seen = new Dictionary<string, string?>();
        var scanned = 0;
        await foreach (var message in client.IterMessagesAsync(entity, minId: 0, limit: maxMessages, cancellationToken))
        {
            scanned++;
            if (message.Links is not { Count: > 0 } links)
            {
                continue;
            }

            foreach (var link in links)
            {
                if (!seen.TryGetValue(link.Url, out var existingLabel) || (existingLabel is null && link.Label is not null))
                {
                    seen[link.Url] = link.Label;
                }
            }
        }

        if (seen.Count == 0)
        {
            console.MarkupLine($"[yellow]Scanned {scanned} message(s), found no links. Nothing to generate.[/]");
            return;
        }

        console.MarkupLine(
            $"Resolving [bold]{seen.Count}[/] distinct link(s) to their real channel name. Each one is a real " +
            "Telegram round-trip, and a link to a channel this account hasn't joined costs a second one on top " +
            "of that -- Telegram can throttle this hard (multi-minute waits between attempts) if several in a " +
            "row hit that unjoined fallback. Not stuck, just being made to wait it out rather than hammering " +
            "Telegram's API...");
        var urls = seen.Keys.ToList();
        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            console.Markup($"  [dim]({i + 1}/{urls.Count}) resolving {Markup.Escape(url)}...[/]");

            // Each of these links is itself a Telegram chat -- resolving
            // it directly gives the *actual* channel title, which beats
            // the message-text label heuristic whenever it's available
            // (a reminder repost with no title line above it, a label
            // that's really just a sentence, etc.). Never joins, same
            // guarantee as every other ResolveEntityAsync call in this
            // app -- only falls back to the heuristic label if the link
            // is invalid/expired/inaccessible.
            try
            {
                var linkedEntity = await client.ResolveEntityAsync(url, cancellationToken);
                seen[url] = linkedEntity.DisplayName;
                console.MarkupLine($" [green]{Markup.Escape(linkedEntity.DisplayName)}[/]");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep whatever heuristic label (possibly null) was already there.
                var fallback = seen[url] ?? "the link itself for the folder name";
                var reason = ex.Message.Contains("has not joined", StringComparison.OrdinalIgnoreCase)
                    ? "not a member of this channel"
                    : ex.Message;
                console.MarkupLine($" [yellow]couldn't resolve ({Markup.Escape(reason)}) -- using {Markup.Escape(fallback)}[/]");
            }
        }

        Directory.CreateDirectory(uploadsDir);
        Directory.CreateDirectory(exportsDir);

        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yaml = new StringBuilder();
        yaml.AppendLine("upload_jobs:");

        var created = 0;
        foreach (var (url, label) in seen)
        {
            var slug = MakeUniqueSlug(label, url, usedSlugs);
            var sourceDir = Path.Combine(uploadsDir, slug);
            Directory.CreateDirectory(sourceDir);
            created++;

            if (label is not null)
            {
                yaml.AppendLine($"  # {label}");
            }

            yaml.AppendLine($"  - source_dir: uploads/{slug}");
            yaml.AppendLine($"    target_chat: \"{url}\"");
            yaml.AppendLine("    recursive: false");
        }

        var safeName = string.Concat(entity.DisplayName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var yamlPath = Path.Combine(exportsDir, $"upload_jobs_{safeName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.yaml");
        await File.WriteAllTextAsync(yamlPath, yaml.ToString(), cancellationToken);

        console.MarkupLine($"[bold]Scanned {scanned}[/] message(s), [green]{seen.Count}[/] distinct link(s).");
        console.MarkupLine($"Created [bold]{created}[/] folder(s) under [bold]{Markup.Escape(uploadsDir)}[/] (empty — drop files in to upload them).");
        console.MarkupLine($"Wrote [bold]{Markup.Escape(yamlPath)}[/] — review it and paste whichever entries you want into " +
            "config/channels.yaml's own upload_jobs: list. Nothing was written to channels.yaml itself.");
    }

    /// <summary>
    /// <c>--mode links-to-jobs --from-yaml &lt;path&gt;</c>: no Telegram
    /// involved at all -- reads an existing <c>upload_jobs:</c> YAML file
    /// (typically one <see cref="RunAsync"/> generated earlier, then
    /// hand-edited to rename some <c>source_dir</c> entries after a link
    /// failed to auto-resolve to a real channel name) and creates
    /// whichever of its <c>source_dir</c> folders don't already exist.
    /// Purely additive, same guarantee as <see cref="RunAsync"/>'s own
    /// directory creation -- never touches an existing folder/file, never
    /// touches <c>channels.yaml</c>.
    /// </summary>
    public static void SyncFromYaml(string yamlPath, IAnsiConsole console)
    {
        var sourceDirs = UploadJobsYamlReader.ReadSourceDirs(yamlPath);
        if (sourceDirs.Count == 0)
        {
            console.MarkupLine($"[yellow]'{Markup.Escape(yamlPath)}' has no upload_jobs entries -- nothing to create.[/]");
            return;
        }

        var created = 0;
        var alreadyExisted = 0;
        foreach (var sourceDir in sourceDirs)
        {
            if (Directory.Exists(sourceDir))
            {
                alreadyExisted++;
                console.MarkupLine($"  [dim]kept:    {Markup.Escape(sourceDir)} (already exists)[/]");
                continue;
            }

            Directory.CreateDirectory(sourceDir);
            created++;
            console.MarkupLine($"  [green]created: {Markup.Escape(sourceDir)}[/]");
        }

        console.MarkupLine(
            $"[bold]{sourceDirs.Count}[/] entr{(sourceDirs.Count == 1 ? "y" : "ies")} in '{Markup.Escape(yamlPath)}': " +
            $"[green]{created}[/] folder(s) created, [dim]{alreadyExisted}[/] already existed.");
    }

    /// <summary>
    /// Filesystem- and YAML-key-safe folder name from a link's label —
    /// falls back to a short hash of the URL when there's no label (or
    /// two links share the same label), so every entry still gets a
    /// distinct, valid folder rather than colliding or failing.
    /// </summary>
    private static string MakeUniqueSlug(string? label, string url, HashSet<string> usedSlugs)
    {
        var basis = string.IsNullOrWhiteSpace(label) ? url : label;
        var slug = string.Concat(basis.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_");
        }

        slug = slug.Trim('_');
        if (slug.Length == 0)
        {
            slug = "link";
        }

        // The label heuristic (nearest line above the link) occasionally
        // catches a sentence rather than a title -- cap length so that
        // false positive doesn't become a 60+ character folder name.
        const int maxLength = 40;
        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].TrimEnd('_');
        }

        var candidate = slug;
        var suffix = 2;
        while (!usedSlugs.Add(candidate))
        {
            candidate = $"{slug}_{suffix++}";
        }

        return candidate;
    }

    /// <summary>Same title-copy-paste fallback as <see cref="ExportLinksCommand"/> — see its remarks for why.</summary>
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
