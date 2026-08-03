using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Audiobook;

/// <summary>
/// Embeds Artist/AlbumArtist/Album/Title/Track tags into an audio file in
/// place. Implemented against TagLibSharp in Infrastructure. Supports
/// .mp3, .m4a, .m4b — throws for anything else.
/// </summary>
public interface IAudiobookTagger
{
    /// <exception cref="NotSupportedException">The file's extension isn't a supported audio format.</exception>
    void Tag(string filePath, AudiobookMetadata metadata, ParseResult info);
}
