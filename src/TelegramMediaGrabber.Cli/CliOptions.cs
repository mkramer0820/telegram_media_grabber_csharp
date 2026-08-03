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
public sealed record CliOptions(string Mode, int? IntervalSeconds)
{
    private static readonly string[] ValidModes = ["run", "download", "upload", "reprocess", "verify", "watch", "resolve-ids"];

    /// <exception cref="ArgumentException">An unrecognized <c>--mode</c> value, or a non-positive <c>--interval</c>, was given.</exception>
    public static CliOptions Parse(string[] args)
    {
        var mode = "run";
        int? intervalSeconds = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--mode" && i + 1 < args.Length)
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
        }

        if (!ValidModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown --mode '{mode}'. Expected one of: {string.Join(", ", ValidModes)}.");
        }

        return new CliOptions(mode.ToLowerInvariant(), intervalSeconds);
    }
}
