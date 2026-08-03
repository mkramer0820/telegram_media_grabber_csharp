namespace TelegramMediaGrabber.Domain;

/// <summary>
/// Identifies a Telegram message a file/download originated from.
/// Provenance and logging/dedup-key material ONLY — never a source of
/// chapter/episode/volume numbering. See <see cref="ChapterNumber"/> and
/// AGENTS.md §2.
/// </summary>
public readonly record struct MessageReference(long ChatId, int MessageId)
{
    public override string ToString() => $"chat={ChatId} message={MessageId}";
}
