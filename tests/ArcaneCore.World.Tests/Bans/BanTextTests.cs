using ArcaneCore.World.Bans;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// The retail helpers behind the ban commands: TimeStringToSecs and secsToTimeString (vmangos
/// shared/Util.cpp:197-275) and the ExtractArg family (game/Chat/Chat.cpp:2818-2945, 3164-3175).
/// </summary>
public sealed class BanTextTests
{
    [Theory]
    [InlineData("1d12h", 129600u)]
    [InlineData("90s", 90u)]
    [InlineData("1h30m", 5400u)]
    [InlineData("2d", 172800u)]
    [InlineData("1d1d", 172800u)]   // repeated units just add
    [InlineData("5", 0u)]           // digits without a unit contribute nothing
    [InlineData("1x", 0u)]          // any other character: 0 = permanent
    [InlineData("forever", 0u)]
    [InlineData("10m5", 600u)]      // the trailing digits without a unit are dropped
    [InlineData("1d2x3h", 0u)]      // a bad character anywhere returns 0, even after good parts
    [InlineData("", 0u)]
    public void TimeStringToSecs_FollowsVmangos(string text, uint expected)
        => Assert.Equal(expected, BanCommandText.TimeStringToSecs(text));

    [Fact]
    public void TimeStringToSecs_NeverWraps_OverflowIsReportedAndSaturates()
    {
        // 50000d = 4,320,000,000 s, above uint.MaxValue (4,294,967,295): the original wraps to about 25,000 s.
        Assert.False(BanCommandText.TryTimeStringToSecs("50000d", out uint secs));
        Assert.Equal(0u, secs);
        Assert.Equal(uint.MaxValue, BanCommandText.TimeStringToSecs("50000d"));
    }

    [Theory]
    [InlineData("49710d", true)]            // 4,294,944,000 s: fits
    [InlineData("49710d6h28m15s", true)]    // exactly uint.MaxValue
    [InlineData("49710d6h28m16s", false)]   // one past
    [InlineData("49710d49710d", false)]     // the sum overflows
    [InlineData("99999999999s", false)]     // the digit run overflows
    [InlineData("4294967296s", false)]
    [InlineData("1x", true)]                // a bad format is still the retail 0
    public void TryTimeStringToSecs_ReportsOverflow(string text, bool fits)
        => Assert.Equal(fits, BanCommandText.TryTimeStringToSecs(text, out _));

    [Theory]
    [InlineData("1d12h", true)]
    [InlineData("90s", true)]
    [InlineData("5", false)]
    [InlineData("1x", false)]
    [InlineData("forever", false)]
    [InlineData("", false)]
    [InlineData("d", false)]
    [InlineData("10m5", false)]
    public void IsWellFormedDuration_OnlyAcceptsDigitRunsWithUnits(string text, bool expected)
        => Assert.Equal(expected, BanCommandText.IsWellFormedDuration(text));

    [Theory]
    [InlineData(0UL, "0s")]
    [InlineData(45UL, "45s")]
    [InlineData(60UL, "1m")]
    [InlineData(7380UL, "2h3m")]
    [InlineData(86400UL, "1d")]
    [InlineData(90061UL, "1d1h1m1s")]
    [InlineData(172800UL, "2d")]
    public void SecsToTimeString_ShortText_FollowsVmangos(ulong secs, string expected)
        => Assert.Equal(expected, BanCommandText.SecsToTimeString(secs));

    [Fact]
    public void ExtractArg_SplitsLiteralTokens_AndTheReasonIsOnlyTheFirstToken()
    {
        string rest = "VICTIM 1d spam and more words";
        Assert.Equal("VICTIM", BanCommandText.ExtractArg(ref rest));
        Assert.Equal("1d", BanCommandText.ExtractArg(ref rest));
        Assert.Equal("spam", BanCommandText.ExtractArg(ref rest)); // retail: one token
        Assert.Equal("and more words", rest);
    }

    [Theory]
    [InlineData("'two words' tail", "two words", "tail")]
    [InlineData("\"quoted reason\"", "quoted reason", "")]
    [InlineData("[bracketed one] x", "bracketed one", "x")]
    [InlineData("''", "", "")]
    public void ExtractArg_QuotedTokens(string input, string token, string remainder)
    {
        string rest = input;
        Assert.Equal(token, BanCommandText.ExtractArg(ref rest));
        Assert.Equal(remainder, rest);
    }

    [Theory]
    [InlineData("'unterminated")]
    [InlineData("'closed'glued")]  // a closing quote must end the text or precede whitespace
    [InlineData("")]
    [InlineData("|cffff0000link|r")] // a |-led token is a link: rejected without a link context
    public void ExtractArg_RejectsMalformedInput(string input)
    {
        string rest = input;
        Assert.Null(BanCommandText.ExtractArg(ref rest));
    }

    [Fact]
    public void ExtractLiteralArg_DoublePipeIsOneEscapedPipe_AndExtraSpacesAreSkipped()
    {
        string rest = "||x   y";
        Assert.Equal("|x", BanCommandText.ExtractLiteralArg(ref rest));
        Assert.Equal("y", rest);
    }

    [Fact]
    public void Texts_AreTheMangosStringRows()
    {
        Assert.Equal("Account X has never been banned", string.Format(BanCommandText.BanInfoNoAccountBan, "X"));
        Assert.Equal("X is banned for 1d. Reason: spam.", string.Format(BanCommandText.YouBanned, "X", "1d", "spam"));
        Assert.Equal("X is banned permanently for spam.", string.Format(BanCommandText.YouPermBanned, "X", "spam"));
        Assert.Equal("account X not found", string.Format(BanCommandText.BanNotFound, "account", "X"));
        Assert.Equal("X unbanned.", string.Format(BanCommandText.Unbanned, "X"));
    }
}
