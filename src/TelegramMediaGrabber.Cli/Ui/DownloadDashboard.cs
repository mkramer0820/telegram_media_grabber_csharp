using Spectre.Console;
using Spectre.Console.Rendering;
using TelegramMediaGrabber.Application.Progress;

namespace TelegramMediaGrabber.Cli.Ui;

/// <summary>
/// Live Spectre.Console dashboard for download/watch mode. Implements
/// <see cref="IDownloadProgressReporter"/> so it can be handed directly to
/// <c>DownloadManager</c> without the Application layer ever referencing
/// Spectre.Console (AGENTS.md §1.3 / §4.1 dependency direction).
/// </summary>
/// <remarks>
/// Earlier version kept only one shared "current activity" line for every
/// channel combined -- with several channels downloading concurrently
/// (<c>max_concurrent_downloads</c>), that line was overwritten many times
/// a second, so a file completion or a channel-resolve error was visible
/// for a fraction of a second before the next channel's update erased it.
/// This version tracks live status per channel (so concurrent channels
/// don't stomp on each other) and keeps a bounded, retained feed of recent
/// completions/errors instead of a single overwritten line, so nothing
/// flashes past unseen.
/// </remarks>
public sealed class DownloadDashboard(LiveDisplayContext context) : IDownloadProgressReporter
{
    private const int MaxActivityLines = 12;

    private sealed class ChannelState
    {
        public int Scanned;
        public int Downloaded;
        public bool Done;
        public string? CurrentFile;
        public double CurrentPercent;
        public int ErrorCount;
        public string? LastError;
        public string? LastEpisode;
        public DateTimeOffset? LastUpdateUtc;
    }

    /// <summary>Renders how long ago a channel last had any activity — the "is this channel maybe broken/renamed?" signal at a glance.</summary>
    private static string FormatAge(DateTimeOffset? lastUpdateUtc)
    {
        if (lastUpdateUtc is not { } since)
        {
            return "never";
        }

        var age = DateTimeOffset.UtcNow - since;
        return age switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)age.TotalHours}h ago",
            _ => $"{(int)age.TotalDays}d ago",
        };
    }

    private readonly Dictionary<string, ChannelState> _channels = [];
    private readonly LinkedList<string> _recentActivity = new();

    /// <summary>Registers every configured channel up front (Scanned=0, not started) so the table shows the full list immediately, not just channels that have already produced an event.</summary>
    public void SeedChannels(IEnumerable<string> channelNames)
    {
        foreach (var name in channelNames)
        {
            _channels.TryAdd(name, new ChannelState());
        }

        Refresh();
    }

    /// <summary>Pushes a one-off line into the retained activity feed (e.g. upload-loop scan announcements in <c>RunCommand</c>) without disturbing per-channel state.</summary>
    public void Note(string markup)
    {
        AddActivity(markup);
        Refresh();
    }

    private ChannelState GetOrAddChannel(string chatName)
    {
        if (!_channels.TryGetValue(chatName, out var state))
        {
            state = new ChannelState();
            _channels[chatName] = state;
        }

        return state;
    }

    private void AddActivity(string markup)
    {
        _recentActivity.AddFirst(markup);
        while (_recentActivity.Count > MaxActivityLines)
        {
            _recentActivity.RemoveLast();
        }
    }

    /// <summary>
    /// Errors first, then still-in-progress channels, then done last —
    /// so when the row budget (<see cref="Render"/>) can't fit everything,
    /// what gets cut is the channels that need no further attention, never
    /// the ones that do.
    /// </summary>
    private static int Priority(ChannelState state) => state.ErrorCount > 0 ? 0 : state.Done ? 2 : 1;

    /// <summary>
    /// A channel with nothing worth looking at right now: no error, not
    /// actively downloading a file, and nothing downloaded from it yet.
    /// With dozens of channels configured, most sit at 0/0 for most of a
    /// run (nothing new posted since last time) — those don't get a row
    /// of their own, only a rolled-up count (<see cref="Render"/>), so the
    /// table only lists channels that actually need a glance.
    /// </summary>
    private static bool IsQuiet(ChannelState state) =>
        state.ErrorCount == 0 && state.CurrentFile is null && state.Downloaded == 0;

    /// <summary>
    /// Best-effort terminal height; a real console always reports one, but
    /// this also runs (via <see cref="AnsiConsole.Live"/>) in contexts
    /// without a proper console handle, where querying it can throw --
    /// fall back to a conservative default rather than let that take the
    /// whole dashboard down.
    /// </summary>
    private static int GetAvailableHeight()
    {
        try
        {
            var height = System.Console.WindowHeight;
            return height > 0 ? height : 30;
        }
        catch
        {
            return 30;
        }
    }

    private IRenderable Render()
    {
        var totalChannels = _channels.Count;
        var doneChannels = _channels.Values.Count(c => c.Done);
        var errorChannels = _channels.Values.Count(c => c.ErrorCount > 0);
        var totalDownloaded = _channels.Values.Sum(c => c.Downloaded);

        var summary = new Markup(
            $"[bold]{totalChannels}[/] channel(s) — [green]{doneChannels} done[/], " +
            $"[yellow]{totalChannels - doneChannels} in progress[/], [red]{errorChannels} with errors[/] — " +
            $"[bold]{totalDownloaded}[/] file(s) downloaded so far");

        // Table chrome (title/header/borders) is ~4 lines; the activity
        // panel (if shown) adds its own line count plus ~3 for
        // header/borders. Whatever's left after those and the summary
        // line is how many channel rows can actually fit on screen.
        var activityLines = Math.Min(_recentActivity.Count, MaxActivityLines);
        var reserved = 1 + 4 + (activityLines > 0 ? activityLines + 3 : 0);
        var maxChannelRows = Math.Max(3, GetAvailableHeight() - reserved);

        var quiet = _channels.Where(kv => IsQuiet(kv.Value)).ToList();
        var noteworthy = _channels
            .Where(kv => !IsQuiet(kv.Value))
            .OrderBy(kv => Priority(kv.Value))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = noteworthy.Take(maxChannelRows).ToList();
        var hidden = noteworthy.Skip(shown.Count).ToList();

        var table = new Table().Title("Channels").Expand();
        table.AddColumn("Channel");
        table.AddColumn(new TableColumn("Scanned").RightAligned());
        table.AddColumn(new TableColumn("Downloaded").RightAligned());
        table.AddColumn("Status");
        table.AddColumn("Current");
        table.AddColumn("Last grabbed");
        table.AddColumn("Last update");

        if (shown.Count == 0)
        {
            table.AddRow(new Markup("[dim]Nothing to report yet — every channel is quiet (see below).[/]"),
                Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty);
        }

        foreach (var (name, state) in shown)
        {
            var status = state.ErrorCount > 0
                ? $"[red]error ({state.ErrorCount})[/]"
                : state.Done ? "[green]done[/]" : "[yellow]scanning[/]";

            var current = state.CurrentFile is { } file
                ? $"{Markup.Escape(file)} ({state.CurrentPercent:F0}%)"
                : state.LastError is { } lastError
                    ? $"[red]{Markup.Escape(lastError)}[/]"
                    : "[dim]-[/]";

            var lastEpisode = state.LastEpisode is { } ep ? Markup.Escape(ep) : "[dim]-[/]";

            table.AddRow(
                Markup.Escape(name), state.Scanned.ToString(), state.Downloaded.ToString(), status, current,
                lastEpisode, FormatAge(state.LastUpdateUtc));
        }

        if (hidden.Count > 0)
        {
            var hiddenErrors = hidden.Count(kv => kv.Value.ErrorCount > 0);
            var hiddenInProgress = hidden.Count(kv => kv.Value.ErrorCount == 0 && !kv.Value.Done);
            var hiddenDone = hidden.Count - hiddenErrors - hiddenInProgress;
            var detail = hiddenErrors > 0 || hiddenInProgress > 0
                ? $"{hiddenErrors} with errors, {hiddenInProgress} in progress, {hiddenDone} done"
                : "all done, no errors";
            var color = hiddenErrors > 0 ? "red" : hiddenInProgress > 0 ? "yellow" : "dim";
            table.AddRow(
                new Markup($"[{color}]+ {hidden.Count} more ({detail}) — window too short to show[/]"),
                Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty);
        }

        if (quiet.Count > 0)
        {
            var quietScanning = quiet.Count(kv => !kv.Value.Done);
            var quietDone = quiet.Count - quietScanning;
            // Oldest quiet channel surfaced explicitly -- a channel that's
            // been quiet the longest is the most likely one to actually be
            // broken (renamed/dead) rather than just having nothing new to
            // post; buried inside a rolled-up count is exactly where you'd
            // otherwise never notice it.
            var stalest = quiet.Select(kv => kv.Value.LastUpdateUtc).Where(t => t is not null).MinBy(t => t);
            var stalestNote = stalest is not null ? $", oldest last update: {FormatAge(stalest)}" : "";
            table.AddRow(
                new Markup($"[dim]+ {quiet.Count} quiet ({quietDone} done, {quietScanning} still scanning) — " +
                    $"nothing downloaded, no errors{stalestNote}[/]"),
                Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty);
        }

        var rows = new List<IRenderable> { summary, table };

        if (_recentActivity.Count > 0)
        {
            var activity = new Panel(string.Join("\n", _recentActivity))
            {
                Header = new PanelHeader("Recent activity"),
                Border = BoxBorder.Rounded,
            };
            rows.Add(activity);
        }

        return new Rows(rows);
    }

    private void Refresh()
    {
        context.UpdateTarget(Render());
        context.Refresh();
    }

    public void OnFileProgress(FileProgress progress)
    {
        var percent = progress.BytesTotal > 0 ? 100.0 * progress.BytesDone / progress.BytesTotal : 0;
        var state = GetOrAddChannel(progress.ChatName);
        state.CurrentFile = progress.FileName;
        state.CurrentPercent = percent;
        state.LastUpdateUtc = DateTimeOffset.UtcNow;
        Refresh();
    }

    public void OnFileComplete(string chatName, int messageId, string finalPath)
    {
        var state = GetOrAddChannel(chatName);
        state.CurrentFile = null;
        state.LastEpisode = Path.GetFileName(finalPath);
        state.LastUpdateUtc = DateTimeOffset.UtcNow;
        AddActivity($"[green]✓[/] {Markup.Escape(chatName)}: {Markup.Escape(state.LastEpisode)}");
        Refresh();
    }

    public void OnFileError(string chatName, int messageId, string error)
    {
        var state = GetOrAddChannel(chatName);
        state.CurrentFile = null;
        state.ErrorCount++;
        state.LastError = error;
        state.LastUpdateUtc = DateTimeOffset.UtcNow;
        var location = messageId > 0 ? $"message {messageId}" : "channel";
        AddActivity($"[red]✗[/] {Markup.Escape(chatName)} ({location}): {Markup.Escape(error)}");
        Refresh();
    }

    public void OnChannelProgress(ChannelProgress progress)
    {
        var state = GetOrAddChannel(progress.ChatName);
        state.Scanned = progress.MessagesScanned;
        state.Downloaded = progress.FilesDownloaded;
        state.Done = progress.Done;
        // Deliberately not touching LastUpdateUtc here: this fires just
        // from scanning/resolving a channel, not from anything new
        // actually being found there -- stamping it on every scan would
        // make a genuinely stale channel look freshly active every time
        // a catch-up pass merely checks it, defeating the point of this
        // field as a "when did this channel last actually have something" signal.
        Refresh();
    }

    public void OnFloodWait(double seconds)
    {
        AddActivity($"[yellow]FloodWait: pausing {seconds:F1}s[/]");
        Refresh();
    }
}
