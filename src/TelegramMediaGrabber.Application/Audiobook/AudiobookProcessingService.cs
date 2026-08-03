using System.Text.RegularExpressions;
using TelegramMediaGrabber.Application.Files;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>
/// Tags and relocates audiobook files. Builds on <see cref="IAudiobookTagger"/>
/// and <see cref="AudiobookNaming"/>; this is the shared primitive both the
/// normal per-download path and the reprocess/verify repair flows use.
/// </summary>
public sealed partial class AudiobookProcessingService(IAudiobookTagger tagger)
{
    // Scans an already-tagged destination filename (e.g. "Example Novel -
    // Ep 0009 - Title.mp3") for its episode number. Deliberately matches
    // only "Ep", never "Vol" — volume numbering is a separate space and
    // must not influence, or be influenced by, chapter-number inference
    // (AGENTS.md §2).
    [GeneratedRegex(@"\bEp\s*(?<episode>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExistingEpisodeTagPattern();

    /// <summary>Returns the episode number already encoded in a tagged filename's stem, if any.</summary>
    public static int? ParseTaggedEpisodeNumber(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = ExistingEpisodeTagPattern().Match(stem);
        return match.Success ? int.Parse(match.Groups["episode"].Value) : null;
    }

    /// <summary>
    /// Infers the next chapter number from files already tagged in
    /// <paramref name="destDir"/>: one past the highest existing "Ep n",
    /// or 1 if the directory doesn't exist or has no recognizably-tagged
    /// files. Last-resort fallback for a filename with no parsable number
    /// at all — see <c>ChapterParsingService.Resolve</c>'s <c>inferNext</c>.
    /// </summary>
    public static ChapterNumber InferNextEpisodeNumber(string destDir)
    {
        if (!Directory.Exists(destDir))
        {
            return ChapterNumber.ForChapter(1);
        }

        var highest = Directory.EnumerateFiles(destDir)
            .Select(ParseTaggedEpisodeNumber)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .DefaultIfEmpty(0)
            .Max();

        return ChapterNumber.ForChapter(highest + 1);
    }

    /// <summary>
    /// Tags <paramref name="filePath"/> in place and moves it to the
    /// destination for <paramref name="info"/>.
    /// </summary>
    /// <remarks>
    /// Unlike a filename-driven flow, this never re-derives <paramref name="info"/>
    /// from <paramref name="filePath"/>'s own name — callers pass it
    /// explicitly. This is what makes it safe for a "verify against
    /// source" correction flow to use: a file being corrected already has
    /// the WRONG number baked into its current name, which a filename
    /// parser would happily re-match if given the chance.
    /// </remarks>
    /// <returns>
    /// The file's new path after tagging and relocation (dedup-suffixed
    /// if the natural destination name is already taken by a different
    /// file — AGENTS.md §3.4: never overwrite).
    /// </returns>
    public string ApplyTagging(string filePath, ParseResult info, AudiobookMetadata metadata, string destRoot)
    {
        tagger.Tag(filePath, metadata, info);

        var extension = Path.GetExtension(filePath);
        var destination = FilenameSanitizer.DedupSuffixedPath(
            AudiobookNaming.BuildDestinationPath(destRoot, metadata, info, extension));

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        MoveAcrossVolumes(filePath, destination);

        return destination;
    }

    /// <summary>
    /// <see cref="File.Move(string, string)"/> equivalent that survives
    /// relocating onto a different filesystem/volume (falls back to
    /// copy+delete), mirroring the Python predecessor's use of
    /// <c>shutil.move</c> instead of a plain rename for exactly this
    /// reason.
    /// </summary>
    private static void MoveAcrossVolumes(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }
}
