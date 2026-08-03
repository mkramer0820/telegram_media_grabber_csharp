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
public sealed record TelegramMessage(
    int Id,
    long ChatId,
    DateTimeOffset Date,
    string? DocumentFileName,
    bool HasAudio,
    bool HasVideo,
    bool HasPhoto,
    bool HasDocument)
{
    /// <summary>
    /// Best-effort filename for this message's media, before sanitization
    /// — falls back to "{ChatId}_{Id}" when no document filename is
    /// present (mirrors the Python predecessor's derive_filename).
    /// </summary>
    public string DeriveFilename() => DocumentFileName ?? $"{ChatId}_{Id}";
}
