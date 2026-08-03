namespace TelegramMediaGrabber.Domain;

/// <summary>
/// A chapter or volume's number, scoped to its <see cref="ContentUnitKind"/>.
/// </summary>
/// <remarks>
/// Deliberately has no public constructor taking a bare <see cref="int"/>
/// from arbitrary call sites, and no conversion from
/// <see cref="MessageReference"/>. The only ways to obtain one are
/// <see cref="ForChapter"/>/<see cref="ForVolume"/> (explicit, intentional
/// construction — used by filename parsers and inference) or an override.
/// This is the structural fix for the Python predecessor's bug where a
/// Telegram message ID was used as a chapter number: there is no code
/// path here by which a <see cref="MessageReference"/> can become a
/// <see cref="ChapterNumber"/>. See PROJECT_STATE.md §10 and AGENTS.md §2.
/// </remarks>
public readonly record struct ChapterNumber
{
    public int Value { get; }

    public ContentUnitKind Kind { get; }

    private ChapterNumber(int value, ContentUnitKind kind)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Chapter/volume numbers must be non-negative.");
        }

        Value = value;
        Kind = kind;
    }

    /// <summary>Creates a chapter-kind number (4-digit padding, "Ep" label).</summary>
    public static ChapterNumber ForChapter(int value) => new(value, ContentUnitKind.Chapter);

    /// <summary>Creates a volume-kind number (2-digit padding, "Vol" label).</summary>
    public static ChapterNumber ForVolume(int value) => new(value, ContentUnitKind.Volume);

    /// <summary>"Ep" for a chapter, "Vol" for a volume.</summary>
    public string Label => Kind switch
    {
        ContentUnitKind.Chapter => "Ep",
        ContentUnitKind.Volume => "Vol",
        _ => throw new InvalidOperationException($"Unhandled {nameof(ContentUnitKind)}: {Kind}"),
    };

    /// <summary>4 for a chapter (chapter counts run into the thousands), 2 for a volume (books rarely exceed double digits).</summary>
    public int PadWidth => Kind switch
    {
        ContentUnitKind.Chapter => 4,
        ContentUnitKind.Volume => 2,
        _ => throw new InvalidOperationException($"Unhandled {nameof(ContentUnitKind)}: {Kind}"),
    };

    /// <summary>The number left-padded with zeros to <see cref="PadWidth"/>, e.g. "0007" or "01".</summary>
    public string Padded => Value.ToString().PadLeft(PadWidth, '0');

    public override string ToString() => $"{Label} {Value}";
}
