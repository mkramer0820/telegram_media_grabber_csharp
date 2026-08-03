using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Parsing;

/// <summary>
/// One user-configured override for a single file (CSHARP_PORT_GUIDE.md
/// §2). Matched by the exact original filename — no wildcard/folder-level
/// rules in this version, deliberately (see the guide for why).
/// </summary>
/// <param name="Match">The exact original filename this override applies to.</param>
/// <param name="Skip">If true, this file is never processed at all; <see cref="Kind"/>/<see cref="Number"/> must be null.</param>
/// <param name="Kind">Required unless <paramref name="Skip"/> is true.</param>
/// <param name="Number">Required unless <paramref name="Skip"/> is true.</param>
/// <param name="Subtitle">Optional subtitle to apply.</param>
public sealed record OverrideEntry(
    string Match,
    bool Skip,
    ContentUnitKind? Kind,
    int? Number,
    string? Subtitle)
{
    /// <summary>Fails fast on a malformed entry — mirrors the "reject unknown/invalid config" philosophy in AGENTS.md §7.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Match))
        {
            throw new InvalidOperationException("An override entry's 'match' filename must not be empty.");
        }

        if (Skip)
        {
            if (Kind is not null || Number is not null || Subtitle is not null)
            {
                throw new InvalidOperationException(
                    $"Override for '{Match}' has skip: true but also specifies kind/number/subtitle — remove one or the other.");
            }

            return;
        }

        if (Kind is null || Number is null)
        {
            throw new InvalidOperationException(
                $"Override for '{Match}' must specify both 'kind' and 'number' unless 'skip: true' is set.");
        }
    }
}
