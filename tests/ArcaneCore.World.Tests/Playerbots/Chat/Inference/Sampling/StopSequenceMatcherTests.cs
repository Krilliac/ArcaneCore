// Ported from Krilliac/Sonder-Inference src/sampling/tests/test_stop.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>Text-level stop sequences: hold back partial matches across pieces, earliest/longest wins, flush, reset, UTF-8 splits.</summary>
public sealed class StopSequenceMatcherTests
{
    [Fact]
    public void NoStopSequencesStreamsEverything()
    {
        var m = new StopSequenceMatcher(Array.Empty<string>());
        (StopCheck check, string emit) = m.Feed("hello");
        Assert.False(check.Stopped);
        Assert.Equal("hello", emit);
        Assert.Equal(string.Empty, m.FlushText());
    }

    [Fact]
    public void MatchInsideASinglePieceEmitsThePrefixOnly()
    {
        var m = new StopSequenceMatcher(["</s>"]);
        (StopCheck check, string emit) = m.Feed("abc</s>def");
        Assert.True(check.Stopped);
        Assert.Equal(0, check.StopIndex);
        Assert.Equal("abc", emit);
        Assert.True(m.Stopped);
    }

    [Fact]
    public void MatchSpanningPiecesHoldsBackThePartialPrefix()
    {
        var m = new StopSequenceMatcher(["User:"]);
        (StopCheck check, string emit) = m.Feed("Hi Us");
        Assert.False(check.Stopped);
        Assert.Equal("Hi ", emit);
        (check, emit) = m.Feed("er");
        Assert.False(check.Stopped);
        Assert.Equal(string.Empty, emit);
        (check, emit) = m.Feed(": more");
        Assert.True(check.Stopped);
        Assert.Equal(string.Empty, emit);
    }

    [Fact]
    public void HeldTextIsReleasedWhenThePartialMatchDiverges()
    {
        var m = new StopSequenceMatcher(["User:"]);
        (_, string emit) = m.Feed("Use");
        Assert.Equal(string.Empty, emit);
        (StopCheck check, string emit2) = m.Feed("ful");
        Assert.False(check.Stopped);
        Assert.Equal("Useful", emit2);
    }

    [Fact]
    public void FlushReturnsHeldBackTextAtEndOfGeneration()
    {
        var m = new StopSequenceMatcher(["###"]);
        (_, string emit) = m.Feed("done #");
        Assert.Equal("done ", emit);
        Assert.Equal("#", m.FlushText());
        Assert.Equal(string.Empty, m.FlushText());
    }

    [Fact]
    public void EarliestMatchWinsTiesPreferTheLongestSequence()
    {
        var m = new StopSequenceMatcher(["cd", "b"]);
        (StopCheck check, string emit) = m.Feed("abcd");
        Assert.True(check.Stopped);
        Assert.Equal(1, check.StopIndex);
        Assert.Equal("a", emit);

        var t = new StopSequenceMatcher(["ab", "abc"]);
        (check, emit) = t.Feed("xabcz");
        Assert.True(check.Stopped);
        Assert.Equal(1, check.StopIndex);
        Assert.Equal("x", emit);
    }

    [Fact]
    public void StopAtTheVeryStartEmitsNothing()
    {
        var m = new StopSequenceMatcher(["\n\n"]);
        (StopCheck check, string emit) = m.Feed("\n\nrest");
        Assert.True(check.Stopped);
        Assert.Equal(string.Empty, emit);
    }

    [Fact]
    public void FeedingAfterStopKeepsReportingStoppedAndResetReArms()
    {
        var m = new StopSequenceMatcher(["x"]);
        Assert.True(m.Feed("x").Check.Stopped);
        (StopCheck again, string emit) = m.Feed("more");
        Assert.True(again.Stopped);
        Assert.Equal(string.Empty, emit);
        m.Reset();
        Assert.False(m.Stopped);
        Assert.Equal("abc", m.Feed("abc").Emit);
    }

    [Fact]
    public void EmptyStopStringsAreIgnored()
    {
        var m = new StopSequenceMatcher(["", "END"]);
        Assert.Single(m.Stops);
        (StopCheck check, string emit) = m.Feed("text");
        Assert.False(check.Stopped);
        Assert.Equal("text", emit);
    }

    [Fact]
    public void MultiByteUtf8StopSequencesSplitAcrossPieces()
    {
        // em dash (E2 80 94) + "END", fed with the character split across pieces.
        var m = new StopSequenceMatcher([new byte[] { 0xE2, 0x80, 0x94, (byte)'E', (byte)'N', (byte)'D' }]);
        StopCheck check = m.Feed([(byte)'a', 0xE2, 0x80]);
        Assert.False(check.Stopped);
        Assert.Equal("a"u8.ToArray(), m.Emitted.ToArray());
        check = m.Feed([0x94, (byte)'E', (byte)'N']);
        Assert.False(check.Stopped);
        Assert.True(m.Emitted.IsEmpty);
        check = m.Feed("D!"u8);
        Assert.True(check.Stopped);
        Assert.True(m.Emitted.IsEmpty);
    }

    [Fact]
    public void OverlappingPrefixesHoldTheLongestCandidate()
    {
        var m = new StopSequenceMatcher(["aab"]);
        Assert.Equal(string.Empty, m.Feed("aa").Emit);
        Assert.Equal("a", m.Feed("a").Emit); // "aaa": the suffix "aa" could still start "aab"
        (StopCheck check, string emit) = m.Feed("b");
        Assert.True(check.Stopped);
        Assert.Equal(string.Empty, emit);
    }

    [Fact]
    public void ChatTemplateStopSpanningManyTokensIsNeverLeaked()
    {
        // ArcaneCore: a role marker produced one character per token must not reach the chat line.
        var m = new StopSequenceMatcher(["<|im_end|>"]);
        string streamed = string.Empty;
        foreach (string piece in new[] { "Hail", ", friend", "<", "|", "im", "_end", "|", ">" })
        {
            (StopCheck check, string emit) = m.Feed(piece);
            streamed += emit;
            if (check.Stopped)
            {
                break;
            }
        }

        Assert.True(m.Stopped);
        Assert.Equal("Hail, friend", streamed);
    }
}
