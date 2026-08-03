using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// In-memory <see cref="IOverrideLookup"/> built from one channel's
/// configured <see cref="OverrideEntry"/> list.
/// </summary>
public sealed class ChannelOverrideLookup : IOverrideLookup
{
    private readonly IReadOnlyDictionary<string, OverrideEntry> _byFilename;

    /// <exception cref="InvalidOperationException">
    /// Thrown if any entry fails <see cref="OverrideEntry.Validate"/>, or
    /// if two entries share the same <see cref="OverrideEntry.Match"/>
    /// filename — duplicates must fail config loading loudly, never
    /// silently pick one (AGENTS.md §7).
    /// </exception>
    public ChannelOverrideLookup(IEnumerable<OverrideEntry> overrides)
    {
        var byFilename = new Dictionary<string, OverrideEntry>(StringComparer.Ordinal);
        foreach (var entry in overrides)
        {
            entry.Validate();
            if (!byFilename.TryAdd(entry.Match, entry))
            {
                throw new InvalidOperationException(
                    $"Duplicate override 'match' filename: '{entry.Match}'. Each override must be unique per channel.");
            }
        }

        _byFilename = byFilename;
    }

    public bool ShouldSkip(string rawFilename) =>
        _byFilename.TryGetValue(rawFilename, out var entry) && entry.Skip;

    public ParseResult? TryGetOverride(string rawFilename)
    {
        if (!_byFilename.TryGetValue(rawFilename, out var entry) || entry.Skip)
        {
            return null;
        }

        // Kind/Number are guaranteed non-null here by Validate() above.
        var number = entry.Kind == ContentUnitKind.Volume
            ? ChapterNumber.ForVolume(entry.Number!.Value)
            : ChapterNumber.ForChapter(entry.Number!.Value);

        return new ParseResult(number, entry.Subtitle, "Override", ParseConfidence.Override);
    }
}
