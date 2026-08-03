namespace TelegramMediaGrabber.Domain;

/// <summary>
/// Author/title metadata for an audiobook-mode channel. Always comes from
/// configuration — never guessed from a filename. See
/// CSHARP_PORT_GUIDE.md §2 (overrides can supply per-file corrections,
/// but author/novel title remain channel-level config, not per-file).
/// </summary>
public sealed record AudiobookMetadata(string Author, string NovelTitle);
