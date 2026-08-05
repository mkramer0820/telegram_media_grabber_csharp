// Entry point: wires config, state, core, and UI layers together. This is
// the only place permitted to construct concrete Infrastructure types
// (AGENTS.md §1.5 dependency-direction rule) and the only place permitted
// to own process-level concerns like signal handling (AGENTS.md §5.4).

using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Spectre.Console;
using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Cli;
using TelegramMediaGrabber.Cli.Commands;
using TelegramMediaGrabber.Infrastructure.Audiobook;
using TelegramMediaGrabber.Infrastructure.Configuration;
using TelegramMediaGrabber.Infrastructure.State;
using TelegramMediaGrabber.Infrastructure.Telegram;

// Anchor the working directory to wherever .env/.env.example actually
// lives (searched upward from the running assembly's own location, not
// wherever the process happened to be launched from). Without this,
// every relative default path below (session file, state DB, config,
// logs) resolves against Environment.CurrentDirectory -- which differs
// between `dotnet run` from a terminal, an IDE's debug launch profile,
// and a published .exe run from its own folder, so the same "relative"
// path silently points at a different location each time. That's exactly
// what caused a real symptom: the Telegram login session file appeared
// to reset and re-prompt for a verification code depending on how the
// process was started, even though a valid session already existed on
// disk -- just not at the path that particular launch was resolving to.
var repoRoot = FindDirectoryContaining(".env", ".env.example");
if (repoRoot is not null)
{
    Directory.SetCurrentDirectory(repoRoot);
}

// Load .env if present, mirroring the Python predecessor's dotenv usage —
// never overrides variables already set in the real environment.
Env.TraversePath().Load();

static string? FindDirectoryContaining(params string[] anyOfFileNames)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !anyOfFileNames.Any(name => File.Exists(Path.Combine(dir.FullName, name))))
    {
        dir = dir.Parent;
    }

    return dir?.FullName;
}

CliOptions cliOptions;
try
{
    cliOptions = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

// --mode reprocess is fully offline and never touches Telegram, so it's
// the one mode that doesn't need real credentials -- everything else
// does. Checked here, before any network/DB work starts, so a first-time
// user gets one clear "here's exactly what to do" message instead of
// whatever cryptic error WTelegramClient would otherwise raise partway
// through connecting.
if (!string.Equals(cliOptions.Mode, "reprocess", StringComparison.OrdinalIgnoreCase))
{
    var placeholderValues = new Dictionary<string, string>
    {
        ["TG_API_ID"] = "123456",
        ["TG_API_HASH"] = "your_api_hash_here",
        ["TG_PHONE"] = "+15551234567",
    };
    var unset = placeholderValues.Keys
        .Where(name =>
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) || value == placeholderValues[name];
        })
        .ToList();

    if (unset.Count > 0)
    {
        // A real terminal (double-clicked exe, or run directly) can prompt
        // right here and write .env itself -- a first-time user gets
        // working credentials in one sitting instead of a "go edit a file
        // and run this again" round trip. A non-interactive launch (piped
        // output, a scheduled task, CI) can't be prompted without risking
        // an indefinite hang waiting for input that will never come, so
        // that path keeps the old static instructions untouched.
        if (Console.IsInputRedirected || !PromptAndWriteEnvValues(unset, repoRoot ?? AppContext.BaseDirectory))
        {
            await Console.Error.WriteLineAsync(
                $"""
                Missing or still-placeholder .env value(s): {string.Join(", ", unset)}.
                  1. Copy .env.example to .env, next to this program, if you haven't already.
                  2. Get TG_API_ID / TG_API_HASH from https://my.telegram.org (API development tools).
                  3. Set TG_PHONE to your account's phone number, with country code (e.g. +15551234567).
                  4. Run this again.
                (--mode reprocess doesn't need any of this — it works fully offline.)
                """);
            return 1;
        }
    }
}

/// <summary>
/// Interactively prompts for each of <paramref name="missingKeys"/> and
/// writes them into <c>.env</c> next to the program (creating it from
/// <c>.env.example</c> first if it doesn't exist yet), then sets them on
/// the current process's environment so execution can continue without a
/// second run. Returns false if the user declines, or a value fails to
/// validate too many times -- callers fall back to the static
/// instructions in that case, never leave the process in a half-set-up
/// state.
/// </summary>
static bool PromptAndWriteEnvValues(IReadOnlyList<string> missingKeys, string envDir)
{
    const string apiCredentialsUrl = "https://my.telegram.org";

    AnsiConsole.MarkupLine("[bold cyan]First-time setup[/]");
    AnsiConsole.MarkupLine("No [bold].env[/] found yet (or it still has the example's placeholder values).");

    if (!AnsiConsole.Confirm("Enter your Telegram API credentials now?"))
    {
        AnsiConsole.MarkupLine("[yellow]Skipped.[/] Copy .env.example to .env and fill it in by hand, then run again.");
        return false;
    }

    if (missingKeys.Contains("TG_API_ID") || missingKeys.Contains("TG_API_HASH"))
    {
        AnsiConsole.MarkupLine(
            $"Don't have an API ID/hash yet? Get one free at [bold link]{apiCredentialsUrl}[/] " +
            "(log in with the same Telegram account, then \"API development tools\") — takes under a minute.");
    }

    var values = new Dictionary<string, string>();
    foreach (var key in missingKeys)
    {
        var prompt = key switch
        {
            "TG_API_ID" => (TextPrompt<string>)new TextPrompt<string>($"[bold]TG_API_ID[/] (numeric, from {apiCredentialsUrl}):")
                .Validate(v => long.TryParse(v.Trim(), out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must be a number.[/]")),
            "TG_API_HASH" => new TextPrompt<string>($"[bold]TG_API_HASH[/] (from {apiCredentialsUrl}):").Secret(),
            "TG_PHONE" => new TextPrompt<string>("[bold]TG_PHONE[/] (with country code, e.g. +15551234567):")
                .Validate(v => v.Trim().StartsWith('+')
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must start with '+' and your country code.[/]")),
            _ => new TextPrompt<string>($"[bold]{Markup.Escape(key)}[/]:"),
        };

        values[key] = AnsiConsole.Prompt(prompt).Trim();
    }

    var envPath = Path.Combine(envDir, ".env");
    var envExamplePath = Path.Combine(envDir, ".env.example");
    if (!File.Exists(envPath) && File.Exists(envExamplePath))
    {
        File.Copy(envExamplePath, envPath);
    }

    foreach (var (key, value) in values)
    {
        SetEnvFileValue(envPath, key, value);
        Environment.SetEnvironmentVariable(key, value);
    }

    AnsiConsole.MarkupLine($"[green]Saved to {Markup.Escape(envPath)}.[/] Continuing...");
    AnsiConsole.WriteLine();
    return true;
}

/// <summary>
/// Targeted per-line replace of <c>KEY=...</c> in an existing <c>.env</c>
/// file -- same approach as <c>ResolveIdsCommand</c>'s config rewrite and
/// for the same reason: preserves every other line's comments/formatting
/// instead of risking them in a full rewrite. Appends the line if the key
/// isn't present at all yet.
/// </summary>
static void SetEnvFileValue(string envPath, string key, string value)
{
    var pattern = new System.Text.RegularExpressions.Regex($"(?m)^{System.Text.RegularExpressions.Regex.Escape(key)}=.*$");
    var text = File.Exists(envPath) ? File.ReadAllText(envPath) : string.Empty;

    if (pattern.IsMatch(text))
    {
        text = pattern.Replace(text, $"{key}={value}", 1);
    }
    else
    {
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            text += Environment.NewLine;
        }

        text += $"{key}={value}{Environment.NewLine}";
    }

    File.WriteAllText(envPath, text);
}

var logFilePath = GetEnv("LOG_FILE_PATH", "logs/app.log");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
// Rotating file only, never console — attaching a console provider here
// would corrupt Spectre.Console's live displays (AGENTS.md §4.2).
builder.Services.AddSerilog(cfg => cfg
    .MinimumLevel.Information()
    .WriteTo.File(
        logFilePath,
        rollingInterval: RollingInterval.Infinite,
        fileSizeLimitBytes: 5 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 5));

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<object>>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var console = AnsiConsole.Console;
console.MarkupLine("[bold cyan]Telegram Batch Media Downloader/Uploader[/]");
logger.LogInformation("Starting in {Mode} mode", cliOptions.Mode);

var channelsConfigPath = GetEnv("CHANNELS_CONFIG_PATH", "config/channels.yaml");
var stateDbPath = GetEnv("STATE_DB_PATH", "data/state.db");
var audiobooksDestDir = GetEnv("LOCAL_MEDIA_SERVER", "downloads/Audiobooks");

ChannelsOptions options;
try
{
    options = new YamlConfigLoader().LoadFile(channelsConfigPath);
    logger.LogInformation(
        "Loaded config from {Path}: {ChannelCount} channel(s), {UploadJobCount} upload job(s)",
        channelsConfigPath, options.Channels.Count, options.UploadJobs.Count);
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to load channels config from {Path}", channelsConfigPath);
    console.MarkupLine($"[red]Failed to load {Markup.Escape(channelsConfigPath)}:[/] {Markup.Escape(ex.Message)}");
    if (!File.Exists(channelsConfigPath))
    {
        console.MarkupLine(
            $"[yellow]No config file there yet — copy [bold]config/channels.example.yaml[/] to " +
            $"[bold]{Markup.Escape(channelsConfigPath)}[/] and edit it, then run again. See CONFIG.md for every field.[/]");
    }

    return 1;
}

if (options.TestMode)
{
    // A fresh, disposable DB every run: real downloads/uploads/tagging
    // still happen, but nothing gets recorded against the real state, so
    // re-testing never skips anything as already-done and never leaves
    // stale records a later production run would trust incorrectly.
    stateDbPath = Path.Combine(Path.GetTempPath(), $"tmg-testmode-{Guid.NewGuid():N}.db");
    logger.LogWarning("test_mode is on: state tracking redirected to disposable {Path}, real state ({RealPath}) untouched.", stateDbPath, GetEnv("STATE_DB_PATH", "data/state.db"));
    console.MarkupLine($"[bold yellow]TEST MODE — state tracking disabled.[/] Nothing recorded against the real state DB this run ([dim]{Markup.Escape(stateDbPath)}[/] instead).");
}

await using var stateRepository = new SqliteStateRepository(stateDbPath);
var tagger = new TagLibAudiobookTagger();

async Task RunOnceAsync()
{
    if (cliOptions.Mode == "reprocess")
    {
        // Fully offline — never constructs a Telegram client at all.
        await new ReprocessCommand(stateRepository, tagger, options, audiobooksDestDir, console)
            .RunAsync(cts.Token);
        return;
    }

    await using var client = new WTelegramClientAdapter();
    logger.LogInformation("Connecting to Telegram...");
    await client.ConnectAndAuthenticateAsync(cts.Token);
    logger.LogInformation("Connected and authenticated.");

    switch (cliOptions.Mode)
    {
        case "upload":
            await new UploadCommand(client, stateRepository, options, console).RunAsync(cts.Token);
            break;
        case "verify":
            await new VerifyCommand(client, stateRepository, tagger, options, audiobooksDestDir, console)
                .RunAsync(cts.Token);
            break;
        case "watch":
            await new WatchCommand(client, stateRepository, tagger, options, audiobooksDestDir, console).RunAsync(cts.Token);
            break;
        case "resolve-ids":
            await new ResolveIdsCommand(client, stateRepository, options, console, cliOptions.Write, channelsConfigPath).RunAsync(cts.Token);
            break;
        case "download":
            await new DownloadCommand(client, stateRepository, tagger, options, audiobooksDestDir, console).RunAsync(cts.Token);
            break;
        default: // "run"
            await new RunCommand(client, stateRepository, tagger, options, audiobooksDestDir, console).RunAsync(cts.Token);
            break;
    }
}

try
{
    if (cliOptions.IntervalSeconds is { } intervalSeconds)
    {
        // --interval re-runs the mode in a loop (e.g. periodic upload_jobs
        // re-scans) instead of running once and exiting. Not meant for
        // "watch", which already runs continuously on its own.
        console.MarkupLine($"Repeating every [bold]{intervalSeconds}s[/] until stopped (Ctrl+C).");
        while (!cts.Token.IsCancellationRequested)
        {
            await RunOnceAsync();
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
        }
    }
    else
    {
        await RunOnceAsync();
    }
}
catch (OperationCanceledException)
{
    logger.LogWarning("Interrupted by user (Ctrl+C) during {Mode} mode", cliOptions.Mode);
    console.MarkupLine("\n[yellow]Interrupted by user, shutting down.[/]");
    return 130;
}
catch (Exception ex)
{
    logger.LogError(ex, "Unhandled error during {Mode} mode", cliOptions.Mode);
    console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

logger.LogInformation("{Mode} mode completed successfully", cliOptions.Mode);
console.MarkupLine("[bold green]Done.[/]");
return 0;

static string GetEnv(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
