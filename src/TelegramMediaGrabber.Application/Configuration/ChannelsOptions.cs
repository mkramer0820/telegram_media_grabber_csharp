namespace TelegramMediaGrabber.Application.Configuration;

/// <summary>
/// Root shape of <c>channels.yaml</c> (PROJECT_STATE.md §7). Unknown
/// top-level keys must fail loading — enforced by the YAML loader in
/// Infrastructure, not here (this type just models the valid shape).
/// </summary>
/// <param name="UploadIntervalSeconds">
/// How often <c>--mode run</c> re-scans <see cref="UploadJobs"/> for new
/// files while it's running. Has no effect if <see cref="UploadJobs"/> is
/// empty (the upload loop doesn't start at all in that case). Uploading
/// stays paced/batched exactly the way a manual <c>--mode upload</c> run
/// is — this only controls how often that same paced scan repeats, not
/// how aggressively any single scan behaves.
/// </param>
/// <param name="TestMode">
/// If true, the composition root (<c>Program.cs</c>) points the state
/// database at a fresh, disposable file instead of the real
/// <c>STATE_DB_PATH</c> — downloads/uploads/tagging still happen for
/// real (so you can verify real output), but nothing gets recorded as
/// "downloaded"/"uploaded" against the real state, so re-running a test
/// never skips anything as already-done and never leaves the real state
/// in a way a later production run would trust incorrectly.
/// </param>
public sealed record ChannelsOptions(
    string DownloadRoot,
    int MaxConcurrentDownloads,
    IReadOnlyList<ChannelOptions> Channels,
    IReadOnlyList<UploadJobOptions> UploadJobs,
    int UploadIntervalSeconds = 600,
    bool TestMode = false)
{
    public static ChannelsOptions Empty { get; } = new(
        DownloadRoot: "downloads",
        MaxConcurrentDownloads: 5,
        Channels: [],
        UploadJobs: []);

    public void Validate()
    {
        if (MaxConcurrentDownloads is < 1 or > 50)
        {
            throw new InvalidOperationException(
                $"'max_concurrent_downloads' must be between 1 and 50, got {MaxConcurrentDownloads}.");
        }

        if (UploadIntervalSeconds < 1)
        {
            throw new InvalidOperationException(
                $"'upload_interval_seconds' must be a positive integer, got {UploadIntervalSeconds}.");
        }

        foreach (var channel in Channels)
        {
            channel.Validate();
        }

        foreach (var job in UploadJobs)
        {
            job.Validate();
        }
    }
}
