using TelegramMediaGrabber.Domain;
using Xunit;

namespace TelegramMediaGrabber.Domain.Tests;

public class ChapterNumberTests
{
    [Fact]
    public void ForChapter_UsesEpLabelAndFourDigitPadding()
    {
        var number = ChapterNumber.ForChapter(7);

        Assert.Equal(ContentUnitKind.Chapter, number.Kind);
        Assert.Equal("Ep", number.Label);
        Assert.Equal(4, number.PadWidth);
        Assert.Equal("0007", number.Padded);
    }

    [Fact]
    public void ForVolume_UsesVolLabelAndTwoDigitPadding()
    {
        var number = ChapterNumber.ForVolume(1);

        Assert.Equal(ContentUnitKind.Volume, number.Kind);
        Assert.Equal("Vol", number.Label);
        Assert.Equal(2, number.PadWidth);
        Assert.Equal("01", number.Padded);
    }

    [Fact]
    public void ChapterAndVolumeWithSameValue_AreNotEqual()
    {
        // Chapter 1 and Volume 1 are unrelated content units and must
        // never compare equal or collide (AGENTS.md §2).
        var chapter = ChapterNumber.ForChapter(1);
        var volume = ChapterNumber.ForVolume(1);

        Assert.NotEqual(chapter, volume);
    }

    [Fact]
    public void NegativeValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChapterNumber.ForChapter(-1));
    }

    [Fact]
    public void PaddedHandlesNumbersWiderThanPadWidth()
    {
        // A chapter count exceeding 9999 must not be truncated.
        var number = ChapterNumber.ForChapter(12345);
        Assert.Equal("12345", number.Padded);
    }
}
