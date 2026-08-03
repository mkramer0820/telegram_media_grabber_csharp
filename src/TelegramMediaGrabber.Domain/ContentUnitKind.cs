namespace TelegramMediaGrabber.Domain;

/// <summary>
/// Distinguishes a single chapter from a whole bundled volume (a compiled
/// book containing many chapters in one file). The two are separate
/// number spaces and separate naming conventions — a chapter numbered 1
/// and a volume numbered 1 are unrelated and must be able to coexist
/// without collision or ambiguity. See AGENTS.md §2 and
/// PROJECT_STATE.md §10 for why this distinction exists as a type rather
/// than a string label.
/// </summary>
public enum ContentUnitKind
{
    Chapter,
    Volume,
}
