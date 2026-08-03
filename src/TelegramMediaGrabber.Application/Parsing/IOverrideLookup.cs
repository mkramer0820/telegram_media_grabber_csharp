using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Resolves per-file metadata overrides for one channel (CSHARP_PORT_GUIDE.md §2).
/// Overrides win over a parsed result, which wins over inference —
/// see <see cref="ChapterParsingService.Resolve"/> for where that
/// precedence is enforced.
/// </summary>
public interface IOverrideLookup
{
    /// <summary>True if this file must never be processed at all.</summary>
    bool ShouldSkip(string rawFilename);

    /// <summary>The explicit override result for this file, if one is configured (and it isn't a skip).</summary>
    ParseResult? TryGetOverride(string rawFilename);
}
