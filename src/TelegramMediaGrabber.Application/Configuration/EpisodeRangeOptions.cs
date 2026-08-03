namespace TelegramMediaGrabber.Application.Configuration;

/// <summary>
/// A channel's requested episode window (e.g. "only episodes 20 through
/// 25"), consumed by <c>DownloadManager</c> via
/// <c>EpisodeRangeExtractor.WantsEpisode</c> to skip messages outside it
/// before they're ever downloaded.
/// </summary>
/// <param name="Start">First episode number wanted, inclusive.</param>
/// <param name="End">Last episode number wanted, inclusive.</param>
public sealed record EpisodeRangeOptions(int Start, int End)
{
    /// <summary>Fails fast on an inverted or non-positive range.</summary>
    public void Validate(string channelName)
    {
        if (Start <= 0 || End <= 0)
        {
            throw new InvalidOperationException(
                $"Channel '{channelName}' has 'episode_range' with a non-positive start/end.");
        }

        if (End < Start)
        {
            throw new InvalidOperationException(
                $"Channel '{channelName}' has 'episode_range' with end ({End}) before start ({Start}).");
        }
    }
}
