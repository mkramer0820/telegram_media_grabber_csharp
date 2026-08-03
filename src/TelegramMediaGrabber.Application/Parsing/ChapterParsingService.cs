using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// Resolves the final <see cref="ParseResult"/> for one file, enforcing
/// the precedence: override wins over a parsed result, which wins over
/// inference. See CSHARP_PORT_GUIDE.md §2.
/// </summary>
public sealed class ChapterParsingService
{
    private readonly IFilenameParser _chain;

    public ChapterParsingService(IFilenameParser? chain = null)
    {
        _chain = chain ?? FilenameParserChain.Default;
    }

    /// <summary>
    /// Resolves what should happen for <paramref name="rawFilename"/>.
    /// </summary>
    /// <param name="rawFilename">The original source filename.</param>
    /// <param name="overrides">
    /// This file's channel's override lookup, or null if the channel has
    /// no overrides configured.
    /// </param>
    /// <param name="inferNext">
    /// Called only when neither an override nor the parser chain produced
    /// a result — resolves the next chapter number from existing library
    /// state (e.g. highest existing "Ep n" + 1). Kept as a caller-supplied
    /// delegate so this service stays pure/synchronous and testable
    /// without a real file system or state store; the caller (an
    /// orchestration service in Infrastructure) supplies the actual
    /// inference logic.
    /// </param>
    /// <returns>
    /// The resolved <see cref="ParseResult"/>, or null if the file should
    /// be skipped entirely (an override with <c>skip: true</c>).
    /// </returns>
    public ParseResult? Resolve(string rawFilename, IOverrideLookup? overrides, Func<ParseResult> inferNext)
    {
        if (overrides is not null && overrides.ShouldSkip(rawFilename))
        {
            return null;
        }

        var overrideResult = overrides?.TryGetOverride(rawFilename);
        if (overrideResult is not null)
        {
            return overrideResult;
        }

        var parsed = _chain.TryParse(rawFilename);
        if (parsed is not null)
        {
            return parsed;
        }

        return inferNext();
    }
}
