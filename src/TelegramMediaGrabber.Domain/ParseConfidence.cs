namespace TelegramMediaGrabber.Domain;

/// <summary>
/// How a <see cref="ParseResult"/>'s number was obtained.
/// </summary>
public enum ParseConfidence
{
    /// <summary>Read directly from an explicit user override.</summary>
    Override,

    /// <summary>Parsed directly out of the source filename.</summary>
    Exact,

    /// <summary>No number was present in the filename; derived from existing library state (highest + 1).</summary>
    Inferred,
}
