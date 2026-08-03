namespace TelegramMediaGrabber.Application.Configuration;

/// <summary>One upload job: routes a local directory to a target chat.</summary>
/// <param name="SourceDir">Local directory scanned for files to upload.</param>
/// <param name="TargetChat">Destination chat ID or "@username".</param>
/// <param name="Recursive">If true, scans SourceDir and all subdirectories.</param>
public sealed record UploadJobOptions(string SourceDir, string TargetChat, bool Recursive)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceDir))
        {
            throw new InvalidOperationException("Upload job 'source_dir' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(TargetChat))
        {
            throw new InvalidOperationException("Upload job 'target_chat' must not be empty.");
        }
    }
}
