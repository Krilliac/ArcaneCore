using ArcaneCore.World.Ops.Announce;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The text rules of <see cref="OpsAnnouncer"/>: no bidi format characters, whole surrogate pairs only, detail capped at 500 characters.</summary>
public sealed class OpsAnnouncerTextTests
{
    private const string Grin = "\uD83D\uDE00"; // U+1F600 as a UTF-16 surrogate pair

    private static void AssertWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), $"lone high surrogate at {i}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), $"lone low surrogate at {i}");
            }
        }
    }

    [Theory]
    [InlineData('\u061C')] // ARABIC LETTER MARK (the one the earlier list missed)
    [InlineData('\u200E')] // LRM
    [InlineData('\u200F')] // RLM
    [InlineData('\u202A')] // LRE
    [InlineData('\u202B')] // RLE
    [InlineData('\u202C')] // PDF
    [InlineData('\u202D')] // LRO
    [InlineData('\u202E')] // RLO
    [InlineData('\u2066')] // LRI
    [InlineData('\u2067')] // RLI
    [InlineData('\u2068')] // FSI
    [InlineData('\u2069')] // PDI
    public void Clean_DropsEveryBidiFormatCharacter(char bidi)
        => Assert.Equal("ab", OpsAnnouncer.Clean($"a{bidi}b", 50));

    [Fact]
    public void Clean_KeepsOrdinaryRightToLeftLetters()
        => Assert.Equal("\u0645\u0631\u062D\u0628\u0627", OpsAnnouncer.Clean("\u0645\u0631\u062D\u0628\u0627", 50));

    [Fact]
    public void Clean_NeverSplitsASurrogatePair()
    {
        Assert.Equal("ab", OpsAnnouncer.Clean("ab" + Grin, 3)); // the pair needs 2 of the 1 remaining
        Assert.Equal("ab" + Grin, OpsAnnouncer.Clean("ab" + Grin, 4));
        Assert.Equal(Grin, OpsAnnouncer.Clean(Grin + Grin, 3));
        Assert.Equal(string.Empty, OpsAnnouncer.Clean(Grin, 1));
    }

    [Fact]
    public void Clean_ReplacesALoneSurrogateWithAQuestionMark()
    {
        Assert.Equal("a?b", OpsAnnouncer.Clean("a\uD83Db", 10));        // high with no low after it
        Assert.Equal("a?b", OpsAnnouncer.Clean("a\uDE00b", 10));        // low with no high before it
        Assert.Equal("a?", OpsAnnouncer.Clean("a\uD83D", 10));          // high at the very end
        Assert.Equal("??", OpsAnnouncer.Clean("\uDE00\uD83D", 10));     // reversed pair is two lone halves
    }

    [Fact]
    public void Clean_EmptyAndNull_GiveEmpty()
    {
        Assert.Equal(string.Empty, OpsAnnouncer.Clean(null, 10));
        Assert.Equal(string.Empty, OpsAnnouncer.Clean("", 10));
    }

    [Fact]
    public void BuildDetail_CutAtTheLimit_NeverEndsInAHalfPair()
    {
        // 497 + ", " puts the field name's pair at indexes 499-500: a plain cut at 500 would keep only its high half.
        string detail = OpsAnnouncer.BuildDetail(new string('x', 497), [new AnnounceField(Grin, "v")]);

        AssertWellFormed(detail);
        Assert.Equal(499, detail.Length);
        Assert.EndsWith(", ", detail);
    }

    [Fact]
    public void BuildDetail_CutAtTheLimit_KeepsAPairThatEndsExactlyAtTheLimit()
    {
        // 496 + ", " = 498, the pair fills 498-499, then more text: the cut at 500 falls after the pair.
        string detail = OpsAnnouncer.BuildDetail(new string('x', 496), [new AnnounceField(Grin, "value")]);

        AssertWellFormed(detail);
        Assert.Equal(500, detail.Length);
        Assert.Contains(Grin, detail);
    }

    [Fact]
    public void BuildDetail_PlainText_IsCutAtExactlyFiveHundred_AndShorterIsUntouched()
    {
        Assert.Equal(500, OpsAnnouncer.BuildDetail(new string('x', 500), [new AnnounceField("a", "b")]).Length);
        Assert.Equal(new string('x', 500), OpsAnnouncer.BuildDetail(new string('x', 500), null));
        Assert.Equal("d, a=b", OpsAnnouncer.BuildDetail("d", [new AnnounceField("a", "b")]));
        Assert.Equal(string.Empty, OpsAnnouncer.BuildDetail(null, null));
    }
}
