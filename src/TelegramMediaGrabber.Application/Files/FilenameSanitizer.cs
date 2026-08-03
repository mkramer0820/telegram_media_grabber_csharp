using System.Text;
using System.Text.RegularExpressions;

namespace TelegramMediaGrabber.Application.Files;

/// <summary>
/// Centralized, OS-safe filename sanitization (AGENTS.md §3.3). Every
/// filename derived from remote/uploader-controlled data MUST pass
/// through <see cref="Sanitize"/> before touching the filesystem — no
/// other type implements its own ad-hoc sanitization.
/// </summary>
public static partial class FilenameSanitizer
{
    private const int MaxFilenameBytes = 255;
    private const string DefaultStem = "file";

    // Illegal on Windows, plus control characters, enforced even on POSIX
    // so output stays portable across platforms.
    [GeneratedRegex("""[<>:"/\\|?*\x00-\x1f]""")]
    private static partial Regex IllegalCharsPattern();

    private static readonly HashSet<string> ReservedWindowsNames =
    [
        "CON", "PRN", "AUX", "NUL",
        .. Enumerable.Range(1, 9).Select(i => $"COM{i}"),
        .. Enumerable.Range(1, 9).Select(i => $"LPT{i}"),
    ];

    /// <summary>
    /// Sanitizes <paramref name="rawName"/> into a safe, portable
    /// filename. Guarantees: no path traversal (only the final path
    /// segment is kept, regardless of host OS path style), no characters
    /// illegal on Windows, no reserved device names, non-empty, and at
    /// most 255 UTF-8 bytes with the extension preserved when truncation
    /// is necessary. Never returns a path containing a directory separator.
    /// </summary>
    public static string Sanitize(string rawName, string fallbackStem = DefaultStem)
    {
        // Strip any directory components from either path style,
        // regardless of host OS, by taking only the final segment.
        var candidate = rawName.Replace('\\', '/');
        candidate = candidate[(candidate.LastIndexOf('/') + 1)..];

        candidate = candidate.Normalize(NormalizationForm.FormC);
        candidate = IllegalCharsPattern().Replace(candidate, "_");
        candidate = candidate.Trim(' ', '.'); // trailing dots/spaces are unsafe on Windows

        if (candidate.Length == 0)
        {
            candidate = fallbackStem;
        }

        var lastDot = candidate.LastIndexOf('.');
        string stem;
        string ext;
        if (lastDot <= 0) // no dot, or a dot only as the first character (no real stem)
        {
            stem = candidate;
            ext = "";
        }
        else
        {
            stem = candidate[..lastDot];
            ext = candidate[(lastDot + 1)..];
        }

        if (ReservedWindowsNames.Contains(stem.ToUpperInvariant()))
        {
            stem = $"{stem}_file";
        }

        return TruncateToByteLimit(stem, ext);
    }

    private static string TruncateToByteLimit(string stem, string ext)
    {
        var suffix = ext.Length > 0 ? $".{ext}" : "";
        var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
        var budget = Math.Max(MaxFilenameBytes - suffixBytes, 1);

        var encoded = Encoding.UTF8.GetBytes(stem);
        if (encoded.Length <= budget)
        {
            return $"{stem}{suffix}";
        }

        var truncated = encoded[..budget];
        // Avoid splitting a multi-byte UTF-8 character in half.
        var end = truncated.Length;
        while (end > 0 && (truncated[end - 1] & 0b1100_0000) == 0b1000_0000)
        {
            end--;
        }

        return $"{Encoding.UTF8.GetString(truncated, 0, end)}{suffix}";
    }

    /// <summary>
    /// Returns a non-colliding path by appending " (n)" before the
    /// extension when <paramref name="basePath"/> already exists — never
    /// overwrites a distinct existing file (AGENTS.md §3.4).
    /// </summary>
    public static string DedupSuffixedPath(string basePath)
    {
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        var directory = Path.GetDirectoryName(basePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);

        var counter = 1;
        while (true)
        {
            var candidate = Path.Combine(directory, $"{stem} ({counter}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }
}
