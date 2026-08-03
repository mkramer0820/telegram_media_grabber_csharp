namespace TelegramMediaGrabber.Domain;

/// <summary>
/// The outcome of parsing a chapter/volume number and subtitle out of a
/// filename (or resolving one from an override or inference step).
/// </summary>
/// <param name="Number">The resolved chapter or volume number.</param>
/// <param name="Subtitle">The chapter/volume's subtitle, if any was present.</param>
/// <param name="MatchedBy">
/// Which parser produced this result (e.g. "ChapterPatternParser",
/// "VolumePatternParser", "BareNumberParser", "Override",
/// "InferNextChapter") — always populated, never null, so "why did this
/// file get tagged this way" is a log line, not an archaeology exercise.
/// See PROJECT_STATE.md §10.
/// </param>
/// <param name="Confidence">How the number was obtained.</param>
public sealed record ParseResult(
    ChapterNumber Number,
    string? Subtitle,
    string MatchedBy,
    ParseConfidence Confidence
);
