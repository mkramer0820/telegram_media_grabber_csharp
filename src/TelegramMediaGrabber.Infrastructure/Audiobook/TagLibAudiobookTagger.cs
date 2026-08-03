using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Infrastructure.Audiobook;

/// <summary>
/// <see cref="IAudiobookTagger"/> implemented against TagLibSharp. Embeds
/// Artist/AlbumArtist/Album/Title/Track into the file's native tag format
/// (ID3v2 for mp3, MP4 atoms for m4a/m4b).
/// </summary>
public sealed class TagLibAudiobookTagger : IAudiobookTagger
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b",
    };

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The file's extension isn't .mp3, .m4a, or .m4b.</exception>
    public void Tag(string filePath, AudiobookMetadata metadata, ParseResult info)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(info);

        var extension = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException(
                $"'{extension}' is not a supported audiobook format. Supported: {string.Join(", ", SupportedExtensions)}.");
        }

        using var file = TagLib.File.Create(filePath);

        file.Tag.Performers = new[] { metadata.Author };
        file.Tag.AlbumArtists = new[] { metadata.Author };
        file.Tag.Album = metadata.NovelTitle;
        file.Tag.Track = checked((uint)info.Number.Value);
        file.Tag.Title = AudiobookNaming.FormatTitle(metadata.NovelTitle, info);

        file.Save();
    }
}
