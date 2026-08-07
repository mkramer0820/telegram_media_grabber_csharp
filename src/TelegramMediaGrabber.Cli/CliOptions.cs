namespace TelegramMediaGrabber.Cli;

/// <summary>Parsed command-line arguments.</summary>
/// <param name="Mode">
/// One of "run" (default — the config-driven catch-up + watch + periodic
/// upload loop), "download", "upload", "reprocess", "verify", "watch",
/// "resolve-ids". The single-purpose modes are manual override/recovery
/// commands; "run" is what config alone is meant to drive.
/// </param>
/// <param name="IntervalSeconds">
/// If set, re-runs <paramref name="Mode"/> in a loop with this many
/// seconds between runs instead of running once and exiting. Meant for
/// <c>upload</c> (periodic re-scan of <c>upload_jobs</c> folders); has no
/// special meaning for <c>watch</c>/<c>run</c>, which already run
/// continuously on their own (<c>run</c>'s upload loop is paced by
/// config's <c>upload_interval_seconds</c> instead).
/// </param>
/// <param name="Write">
/// Only meaningful with <c>--mode resolve-ids</c>: rewrites
/// <c>config/channels.yaml</c> in place with the permanent numeric chat ID
/// for every channel whose configured value (username/link) resolved to
/// something different, recording the original value in a trailing
/// comment on that line so it stays traceable. See
/// <c>ResolveIdsCommand</c>.
/// </param>
/// <param name="Target">
/// Only meaningful with <c>--mode export-links</c>: the chat to scan —
/// numeric ID, "@username", or invite/t.me link. Does not need to be a
/// channel already listed in <c>channels.yaml</c>.
/// </param>
/// <param name="MaxMessages">
/// Only meaningful with <c>--mode export-links</c>: caps how many of the
/// chat's most recent messages are scanned. Unbounded (full history) if
/// omitted.
/// </param>
/// <param name="Help">
/// True if <c>--help</c>/<c>-h</c> was passed anywhere in the arguments.
/// Checked before mode validation, so <c>--help</c> always works even
/// alongside a missing/invalid <c>--mode</c> — the caller should print
/// <see cref="UsageText"/> and exit without doing anything else (no
/// config load, no Telegram connection).
/// </param>
/// <param name="FromYaml">
/// Only meaningful with <c>--mode links-to-jobs</c>: an alternative to
/// <c>--target</c> that skips scanning Telegram entirely. Points at an
/// existing (optionally hand-edited) <c>upload_jobs:</c> YAML file — e.g.
/// one this same mode generated earlier, then renamed some entries in —
/// and just creates whichever <c>source_dir</c> folders it lists that
/// don't already exist. Purely a filesystem sync; never touches Telegram
/// or requires credentials.
/// </param>
public sealed record CliOptions(
    string Mode, int? IntervalSeconds, bool Write = false, string? Target = null, int? MaxMessages = null,
    bool Help = false, string? FromYaml = null)
{
    private static readonly string[] ValidModes = ["run", "download", "upload", "reprocess", "verify", "watch", "resolve-ids", "export-links", "links-to-jobs"];

    public const string UsageText = """
        Telegram Batch Media Downloader/Uploader

        Usage:
          dotnet run --project src/TelegramMediaGrabber.Cli -- [--mode <mode>] [options]
          TelegramMediaGrabber.Cli.exe [--mode <mode>] [options]         (published build)

        Modes (--mode <mode>; default: run):
          run           Default. Does everything channels.yaml declares in one continuous
                        process: catch-up download, then live watch + periodic upload_jobs
                        scan for as long as the process runs. This is the normal way to run it.
          download      One-shot catch-up scan of every configured channel's backlog, then exit.
          watch         Live-only: downloads new messages as Telegram pushes them. No backlog
                        catch-up -- run --mode download first (or after any downtime) to catch up.
          upload        One-shot scan/send of every configured upload_jobs folder, then exit.
          verify        Online: re-checks already-tagged audiobook episode numbers against
                        Telegram directly and corrects any mismatch.
          reprocess     Offline (no Telegram credentials needed): re-tags/relocates already-
                        downloaded audiobook files without re-contacting Telegram.
          resolve-ids   Resolves every configured channel/upload_jobs chat (however it's
                        written -- username, t.me link, or numeric ID) and prints its permanent
                        numeric Chat ID, resolved title, and link. Add --write to pin the
                        resolved ID into channels.yaml in place.
          export-links  Scans a chat (--target required) for every message containing a link
                        and writes them to a JSON file under exports/. Read-only; the chat
                        doesn't need to be in channels.yaml. See --target/--max-messages below.
          links-to-jobs Scans a chat (--target required) the same way as export-links, then for
                        each distinct link found: creates an empty uploads/<name>/ folder, and
                        writes an upload_jobs: YAML block (to a file under exports/, never to
                        channels.yaml itself) pairing that folder with the link as target_chat.
                        Review the file and paste in whichever entries you actually want. If you
                        then rename any source_dir entries by hand, re-run with --from-yaml
                        <path> instead of --target to create matching folders for your renamed
                        entries -- no Telegram needed, purely a filesystem sync.

        Options:
          --interval <seconds>   Re-run the chosen mode in a loop with this many seconds between
                                  runs, instead of running once and exiting. Meant for --mode
                                  upload; --mode run/watch already run continuously on their own.
          --write                Only with --mode resolve-ids: rewrite channels.yaml in place,
                                  pinning each channel's permanent numeric Chat ID (original
                                  value + resolved title kept in a trailing comment).
          --target <chat>        Required with --mode export-links/links-to-jobs: numeric chat ID,
                                  "@username", invite/t.me link, or exact chat title as shown in Telegram.
          --max-messages <n>     Only with --mode export-links/links-to-jobs: cap how much history
                                  is scanned (default: full history).
          --from-yaml <path>     Only with --mode links-to-jobs: alternative to --target. Reads an
                                  existing (optionally hand-edited) upload_jobs YAML file and creates
                                  whichever source_dir folders it lists that don't exist yet. No
                                  Telegram connection needed.
          --help, -h             Show this message and exit.

        Examples:
          dotnet run --project src/TelegramMediaGrabber.Cli
          dotnet run --project src/TelegramMediaGrabber.Cli -- --mode download
          dotnet run --project src/TelegramMediaGrabber.Cli -- --mode upload --interval 300
          dotnet run --project src/TelegramMediaGrabber.Cli -- --mode resolve-ids --write
          dotnet run --project src/TelegramMediaGrabber.Cli -- --mode export-links --target "@some_channel"
          dotnet run --project src/TelegramMediaGrabber.Cli -- --mode links-to-jobs --target "@some_channel"
          dotnet run --project src/TelegramMediaGrabber.Cli -- --mode links-to-jobs --from-yaml exports/upload_jobs_x.yaml

        See README.md for setup and CONFIG.md for every channels.yaml field.
        """;

    /// <exception cref="ArgumentException">An unrecognized <c>--mode</c> value, or a non-positive <c>--interval</c>, was given.</exception>
    public static CliOptions Parse(string[] args)
    {
        var mode = "run";
        int? intervalSeconds = null;
        var write = false;
        string? target = null;
        int? maxMessages = null;
        var help = false;
        string? fromYaml = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                help = true;
            }
            else if (args[i] is "--mode" && i + 1 < args.Length)
            {
                mode = args[i + 1];
                i++;
            }
            else if (args[i] is "--interval" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out var seconds) || seconds <= 0)
                {
                    throw new ArgumentException($"--interval must be a positive number of seconds, got '{args[i + 1]}'.");
                }

                intervalSeconds = seconds;
                i++;
            }
            else if (args[i] is "--write")
            {
                write = true;
            }
            else if (args[i] is "--target" && i + 1 < args.Length)
            {
                target = args[i + 1];
                i++;
            }
            else if (args[i] is "--max-messages" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out var max) || max <= 0)
                {
                    throw new ArgumentException($"--max-messages must be a positive integer, got '{args[i + 1]}'.");
                }

                maxMessages = max;
                i++;
            }
            else if (args[i] is "--from-yaml" && i + 1 < args.Length)
            {
                fromYaml = args[i + 1];
                i++;
            }
        }

        if (help)
        {
            return new CliOptions(mode.ToLowerInvariant(), intervalSeconds, write, target, maxMessages, Help: true, FromYaml: fromYaml);
        }

        if (!ValidModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown --mode '{mode}'. Expected one of: {string.Join(", ", ValidModes)}. Try --help.");
        }

        if (string.Equals(mode, "export-links", StringComparison.OrdinalIgnoreCase) && target is null)
        {
            throw new ArgumentException("--mode export-links requires --target <chat id, @username, or invite link>.");
        }

        if (string.Equals(mode, "links-to-jobs", StringComparison.OrdinalIgnoreCase) && target is null && fromYaml is null)
        {
            throw new ArgumentException(
                "--mode links-to-jobs requires either --target <chat id, @username, or invite link> " +
                "or --from-yaml <path to an existing upload_jobs YAML file>.");
        }

        return new CliOptions(mode.ToLowerInvariant(), intervalSeconds, write, target, maxMessages, FromYaml: fromYaml);
    }
}
