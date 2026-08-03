namespace TelegramMediaGrabber.Application.Telegram;

/// <summary>A resolved Telegram chat/channel.</summary>
/// <param name="Id">Permanent numeric chat ID — the only durable identifier; usernames/invite links can change or expire.</param>
/// <param name="DisplayName">Title at resolution time.</param>
/// <param name="Username">Public @handle at resolution time, if any (null for chats with no public username, e.g. most invite-link-only private channels).</param>
/// <param name="Kind">"channel", "group", or "user" — best-effort, for identifying an entry later; not used for any resolution/download logic.</param>
public sealed record TelegramEntity(long Id, string DisplayName, string? Username = null, string? Kind = null);
