using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Application.Tests.Parsing;

public class ChapterParsingServiceTests
{
    private readonly ChapterParsingService _service = new();

    private static ParseResult InferredFallback() =>
        new(ChapterNumber.ForChapter(999), null, "InferNextChapter", ParseConfidence.Inferred);

    [Fact]
    public void Resolve_OverrideWinsOverParsedResult()
    {
        // "Ep 5 - Title.mp3" would parse cleanly to chapter 5, but an
        // override for this exact file must win anyway.
        var overrides = new ChannelOverrideLookup(
        [
            new OverrideEntry("Ep 5 - Title.mp3", Skip: false, ContentUnitKind.Chapter, 999, "Overridden"),
        ]);

        var result = _service.Resolve("Ep 5 - Title.mp3", overrides, InferredFallback);

        Assert.NotNull(result);
        Assert.Equal(999, result.Number.Value);
        Assert.Equal(ParseConfidence.Override, result.Confidence);
    }

    [Fact]
    public void Resolve_ParsedResultWinsOverInference()
    {
        var result = _service.Resolve("Ep 5 - Title.mp3", overrides: null, InferredFallback);

        Assert.NotNull(result);
        Assert.Equal(5, result.Number.Value);
        Assert.Equal(ParseConfidence.Exact, result.Confidence);
    }

    [Fact]
    public void Resolve_FallsBackToInferenceWhenNothingElseMatches()
    {
        var result = _service.Resolve("totally_untitled_file.mp3", overrides: null, InferredFallback);

        Assert.NotNull(result);
        Assert.Equal(999, result.Number.Value);
        Assert.Equal(ParseConfidence.Inferred, result.Confidence);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenOverrideSkipsFile()
    {
        var overrides = new ChannelOverrideLookup(
        [
            new OverrideEntry("duplicate_upload.mp3", Skip: true, null, null, null),
        ]);

        var result = _service.Resolve("duplicate_upload.mp3", overrides, InferredFallback);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_SkipShortCircuitsBeforeInferenceIsInvoked()
    {
        var overrides = new ChannelOverrideLookup(
        [
            new OverrideEntry("duplicate_upload.mp3", Skip: true, null, null, null),
        ]);
        var inferenceCalled = false;

        ParseResult InferenceSpy()
        {
            inferenceCalled = true;
            return InferredFallback();
        }

        _service.Resolve("duplicate_upload.mp3", overrides, InferenceSpy);

        Assert.False(inferenceCalled);
    }
}
