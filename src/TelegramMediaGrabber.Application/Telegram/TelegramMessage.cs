namespace TelegramMediaGrabber.Application.Telegram;

/// <summary>
/// The subset of a Telegram message this app needs — deliberately a thin
/// projection, not the underlying MTProto library's message type, so
/// <c>Application</c> never depends on a specific Telegram library.
/// </summary>
/// <param name="Id">Message ID within its chat.</param>
/// <param name="ChatId">Owning chat ID.</param>
/// <param name="Date">When the message was sent (UTC).</param>
/// <param name="DocumentFileName">The document's own filename, if any (used for episode/volume parsing).</param>
/// <param name="HasAudio">True if the message carries a Telegram "audio" (voice/music) attachment.</param>
/// <param name="HasVideo">True if the message carries a video attachment.</param>
/// <param name="HasPhoto">True if the message carries a photo attachment.</param>
/// <param name="HasDocument">True if the message carries a generic document attachment.</param>
/// <param name="Text">The message's raw text/caption, if any.</param>
/// <param name="Links">
/// URLs Telegram itself recognized in this message (its own entity
/// parsing — bare auto-detected links and markdown-style
/// <c>[text](url)</c> links alike), each paired with a best-effort label
/// (see <see cref="LinkEntry"/>), in the order they appear. Null/empty
/// for a message with no links. Deliberately one entry per link rather
/// than the message's <see cref="Text"/> alone: a single post commonly
/// lists several distinct items each with their own link (e.g. "Title
/// A\nlink\n\nTitle B\nlink"), and collapsing those into one bag of URLs
/// loses which title belongs to which link.
/// </param>
/// <param name="SenderId">
/// The posting user's ID, if the message came from an identifiable user
/// (null for messages posted "as the channel" itself, e.g. broadcast
/// channel posts).
/// </param>
public sealed record TelegramMessage(
    int Id,
    long ChatId,
    DateTimeOffset Date,
    string? DocumentFileName,
    bool HasAudio,
    bool HasVideo,
    bool HasPhoto,
    bool HasDocument,
    string? Text = null,
    IReadOnlyList<LinkEntry>? Links = null,
    long? SenderId = null)
{
    /// <summary>
    /// Best-effort filename for this message's media, before sanitization
    /// — falls back to "{ChatId}_{Id}" when no document filename is
    /// present (mirrors the Python predecessor's derive_filename).
    /// </summary>
    public string DeriveFilename() => DocumentFileName ?? $"{ChatId}_{Id}";
}

/// <param name="Url">The link itself.</param>
/// <param name="Label">
/// Best-effort caption for this specific link — the nearest non-blank
/// line of text immediately above it in the message (a book/episode
/// title posted just above its own link is the common case this
/// targets). Null if there was no such line, or it looked like another
/// link rather than a title.
/// </param>
public sealed record LinkEntry(string Url, string? Label);
