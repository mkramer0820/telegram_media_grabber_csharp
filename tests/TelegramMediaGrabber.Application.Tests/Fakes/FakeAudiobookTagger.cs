using TelegramMediaGrabber.Application.Audiobook;
using TelegramMediaGrabber.Domain;

namespace TelegramMediaGrabber.Application.Tests.Fakes;

/// <summary>
/// No-op <see cref="IAudiobookTagger"/> fake — records calls, never
/// touches real tag bytes (no TagLibSharp dependency needed in
/// Application tests). Still honors the "unsupported extension throws"
/// part of the interface contract, since orchestration tests
/// (ReprocessService, VerifyService) rely on that error path.
/// </summary>
public sealed class FakeAudiobookTagger : IAudiobookTagger
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b",
    };

    public List<(string FilePath, AudiobookMetadata Metadata, ParseResult Info)> Calls { get; } = [];

    public void Tag(string filePath, AudiobookMetadata metadata, ParseResult info)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(filePath)))
        {
            throw new NotSupportedException($"Unsupported audiobook extension for tagging: {Path.GetExtension(filePath)}");
        }

        Calls.Add((filePath, metadata, info));
    }
}
