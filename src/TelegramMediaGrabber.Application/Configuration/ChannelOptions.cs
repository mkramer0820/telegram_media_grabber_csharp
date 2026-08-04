using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Configuration;

/// <summary>
/// One channel/chat declared in <c>channels.yaml</c>. Mirrors the Python
/// predecessor's <c>ChannelConfig</c> (PROJECT_STATE.md §7) plus the new
/// <see cref="Overrides"/> list (CSHARP_PORT_GUIDE.md §2).
/// </summary>
/// <param name="Id">Telegram chat ID or "@username" — kept as a string; resolved by <c>ITelegramClient</c>.</param>
/// <param name="Name">Human-readable label used in logs/UI.</param>
/// <param name="MediaTypes">Media kinds to download; defaults to Photo+Video+Document if empty.</param>
/// <param name="OutputSubdir">Subdirectory under DownloadRoot files land in first.</param>
/// <param name="MinDate">ISO-8601 date; messages older than this are skipped.</param>
/// <param name="MaxMessages">Caps how many of the channel's most recent messages are fetched per run; null means unbounded.</param>
/// <param name="AutoUploadTarget">If set, every file downloaded from this channel is immediately re-uploaded to this chat (dedup-tracked, independent of upload_jobs).</param>
/// <param name="EpisodeRange">If set, only files whose filename indicates an episode number/range overlapping this range are downloaded.</param>
/// <param name="AudiobookMode">If true, downloaded audio is tagged/relocated.</param>
/// <param name="Metadata">Required when <paramref name="AudiobookMode"/> is true.</param>
/// <param name="Overrides">Per-file metadata overrides for this channel.</param>
/// <param name="LocalOnly">
/// If true, an <paramref name="AudiobookMode"/> channel is still
/// tagged/organized, but relocated under this app's own
/// <c>{download_root}/Audiobooks/{novel_title}/</c> instead of the
/// configured <c>LOCAL_MEDIA_SERVER</c> destination — for a channel you
/// want tagged output from without it ever leaving this app's own folder
/// tree (e.g. no external media-server mount configured, or you
/// deliberately don't want this particular book sent there). No effect
/// on non-audiobook_mode channels, which already stay local.
/// </param>
/// <param name="MediaServerSubdir">
/// If set, used as the exact destination subfolder name (under whichever
/// root applies per <see cref="LocalOnly"/>) instead of deriving one from
/// <c>metadata.novel_title</c> — e.g. to pick a different folder name
/// than the title, or to keep an existing library layout. Author is
/// never part of the destination path (kept in ID3/MP4 tags only, via
/// <c>metadata.author</c>) — most people organize/browse audiobooks by
/// title, not author, so the default layout is
/// <c>{dest_root}/{novel_title}/...</c>, no author-level folder.
/// </param>
public sealed record ChannelOptions(
    string Id,
    string Name,
    IReadOnlyList<MediaType> MediaTypes,
    string OutputSubdir,
    DateOnly? MinDate,
    bool AudiobookMode,
    AudiobookMetadata? Metadata,
    IReadOnlyList<OverrideEntry> Overrides,
    int? MaxMessages = null,
    string? AutoUploadTarget = null,
    EpisodeRangeOptions? EpisodeRange = null,
    bool LocalOnly = false,
    string? MediaServerSubdir = null)
{
    /// <summary>Fails fast on invalid combinations — mirrors the Python model_validator (AGENTS.md §7).</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidOperationException("Channel 'id' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Channel 'name' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(OutputSubdir))
        {
            throw new InvalidOperationException($"Channel '{Name}' must specify 'output_subdir'.");
        }

        if (AudiobookMode && Metadata is null)
        {
            throw new InvalidOperationException(
                $"Channel '{Name}' has audiobook_mode=true but no 'metadata' (author/novel_title) block.");
        }

        if (MaxMessages is <= 0)
        {
            throw new InvalidOperationException($"Channel '{Name}' has 'max_messages' <= 0; it must be a positive integer.");
        }

        EpisodeRange?.Validate(Name);

        // Constructing the lookup validates every override entry and
        // rejects duplicate 'match' filenames.
        _ = new ChannelOverrideLookup(Overrides);
    }
}
