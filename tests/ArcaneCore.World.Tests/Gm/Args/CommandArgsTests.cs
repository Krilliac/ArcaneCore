using ArcaneCore.World.Gm.Args;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Args;

/// <summary>
/// Vectors for the vmangos ChatHandler Extract* grammar (D:\refs\vmangos\src\game\Chat\Chat.cpp:2690-3290)
/// and the Util.cpp time helpers (:197-282). Expected values are derived by reading those functions.
/// </summary>
public sealed class CommandArgsTests
{
    private const string StaffLink = "|cff9d9d9d|Hitem:812:0:0:0:0:0:0:0|h[Glowing Brightwood Staff]|h|r";

    [Fact]
    public void Int32_ReadsANumberFollowedByWhitespace_AndMovesTheCursor()
    {
        var args = new CommandArgs("12 rest");
        Assert.True(args.ExtractInt32(out int value));
        Assert.Equal(12, value);
        Assert.Equal("rest", args.Rest);
    }

    [Theory]
    [InlineData("12abc")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2147483648")]
    public void Int32_RejectsTrailingJunkEmptyAndOverflow_WithoutMovingTheCursor(string text)
    {
        var args = new CommandArgs(text);
        Assert.False(args.ExtractInt32(out int value));
        Assert.Equal(0, value);
        Assert.Equal(text, args.Rest);
    }

    [Fact]
    public void Int32_AcceptsNegativeAndExplicitPlus_UInt32RejectsNegative()
    {
        Assert.True(new CommandArgs("-5").ExtractInt32(out int a));
        Assert.Equal(-5, a);
        Assert.True(new CommandArgs("+7").ExtractInt32(out int b));
        Assert.Equal(7, b);
        Assert.False(new CommandArgs("-5").ExtractUInt32(out _));
        Assert.False(new CommandArgs("4294967296").ExtractUInt32(out _));
        Assert.True(new CommandArgs("4294967295").ExtractUInt32(out uint max));
        Assert.Equal(uint.MaxValue, max);
    }

    [Fact]
    public void OptionalVariants_UseTheDefaultOnlyWhenNothingRemains()
    {
        var empty = new CommandArgs("");
        Assert.True(empty.ExtractOptUInt32(out uint u, 9));
        Assert.Equal(9u, u);
        Assert.True(empty.ExtractOptInt32(out int i, -3));
        Assert.Equal(-3, i);
        Assert.True(empty.ExtractOptFloat(out float f, 1.5f));
        Assert.Equal(1.5f, f);

        var junk = new CommandArgs("x1");
        Assert.False(junk.ExtractOptUInt32(out uint kept, 9));
        Assert.Equal(0u, kept);
    }

    [Theory]
    [InlineData("1e3", 1000f)]
    [InlineData("-8949.95", -8949.95f)]
    [InlineData(".5", 0.5f)]
    [InlineData("3.", 3f)]
    [InlineData("+2.25e-1", 0.225f)]
    public void Float_UsesTheStrtodGrammar(string text, float expected)
    {
        Assert.True(new CommandArgs(text).ExtractFloat(out float value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1.5.2")]
    [InlineData("e5")]
    [InlineData("-")]
    [InlineData("12x")]
    public void Float_RejectsMalformedNumbers(string text)
        => Assert.False(new CommandArgs(text).ExtractFloat(out _));

    [Fact]
    public void Quoted_ReadsGuardedTextAndRequiresWhitespaceAfterTheGuard()
    {
        var a = new CommandArgs("\"Elwynn Forest\" 5");
        Assert.Equal("Elwynn Forest", a.ExtractQuoted());
        Assert.Equal("5", a.Rest);

        var b = new CommandArgs("'a b'");
        Assert.Equal("a b", b.ExtractQuoted());
        Assert.Equal(string.Empty, b.Rest);

        var c = new CommandArgs("[Stormwind] x");
        Assert.Equal("Stormwind", c.ExtractQuoted());

        var d = new CommandArgs("'x y z'q");
        Assert.Null(d.ExtractQuoted());
        Assert.Equal("'x y z'q", d.Rest);

        Assert.Null(new CommandArgs("'unterminated").ExtractQuoted());
        Assert.Null(new CommandArgs("plain").ExtractQuoted());
    }

    [Fact]
    public void Quoted_AsIsKeepsTheWrappers()
    {
        var args = new CommandArgs("[Stormwind] x");
        Assert.Equal("[Stormwind]", args.ExtractQuoted(asis: true));
        Assert.Equal("x", args.Rest);

        var last = new CommandArgs("\"a b\"");
        Assert.Equal("\"a b\"", last.ExtractQuoted(asis: true));
    }

    [Fact]
    public void Literal_StopsAtASpace_RejectsQuotesAndLinks_AndUnescapesDoubleBar()
    {
        var args = new CommandArgs("Hello   world");
        Assert.Equal("Hello", args.ExtractLiteral());
        Assert.Equal("world", args.Rest);

        Assert.Null(new CommandArgs("'x'").ExtractLiteral());
        Assert.Null(new CommandArgs("[x]").ExtractLiteral());
        Assert.Null(new CommandArgs(StaffLink).ExtractLiteral());
        Assert.Equal("|abc", new CommandArgs("||abc def").ExtractLiteral());
    }

    [Fact]
    public void Literal_WithAnExpectedWord_MatchesOnlyThatWholeWord()
    {
        var args = new CommandArgs("triggered now");
        Assert.Equal("triggered", args.ExtractLiteral("triggered"));
        Assert.Equal("now", args.Rest);

        var longer = new CommandArgs("triggeredx");
        Assert.Null(longer.ExtractLiteral("triggered"));
        Assert.Equal("triggeredx", longer.Rest);
    }

    [Fact]
    public void QuotedOrLiteral_FallsBackToTheLiteral()
    {
        Assert.Equal("a b", new CommandArgs("\"a b\"").ExtractQuotedOrLiteral());
        Assert.Equal("word", new CommandArgs("word rest").ExtractQuotedOrLiteral());
    }

    [Theory]
    [InlineData("on", true, true)]
    [InlineData("ON", true, true)]
    [InlineData("off", true, false)]
    [InlineData("OFF", true, false)]
    [InlineData("On", false, false)]
    [InlineData("1", false, false)]
    [InlineData("true", false, false)]
    [InlineData("", false, false)]
    public void OnOff_AcceptsOnlyOnOffInUpperOrLowerCase(string text, bool ok, bool expected)
    {
        bool result = new CommandArgs(text).ExtractOnOff(out bool value);
        Assert.Equal(ok, result);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Link_ReadsAnItemLinkWithKeyAndName()
    {
        var args = new CommandArgs(StaffLink + " 3");
        ChatLink? link = args.ExtractLink();
        Assert.NotNull(link);
        Assert.Equal(StaffLink, link.Text);
        Assert.Equal("Hitem", link.LinkType);
        Assert.Equal("812", link.Key);
        Assert.Equal("Glowing Brightwood Staff", link.Name);
        Assert.Equal("3", args.Rest);
    }

    [Fact]
    public void Link_FiltersByLinkType_AndExposesTheSomethingField()
    {
        var args = new CommandArgs("|cffffffff|Htalent:123:2|h[Improved Rend]|h|r");
        Assert.Null(args.ExtractLink(["Hspell"]));
        ChatLink? link = args.ExtractLink(["Hspell", "Htalent"], wantSomething: true);
        Assert.NotNull(link);
        Assert.Equal(1, link.TypeIndex);
        Assert.Equal("123", link.Key);
        Assert.Equal("2", link.Something);
    }

    [Theory]
    [InlineData("||Hitem:1|h[x]|h|r")]
    [InlineData("|cffffffff|Hitem:812")]
    [InlineData("|cffffffff|Hitem:812|h[x]|h|rjunk")]
    [InlineData("|cffffffff|Hitem:812|h x|h|r")]
    [InlineData("plain")]
    public void Link_RejectsMalformedAndEscapedBars(string text)
        => Assert.Null(new CommandArgs(text).ExtractLink());

    [Fact]
    public void Link_WithoutAColorPartStillParses()
    {
        ChatLink? link = new CommandArgs("|Hitem:25|h[Worn Shortsword]|h|r").ExtractLink();
        Assert.NotNull(link);
        Assert.Equal("25", link.Key);
    }

    [Fact]
    public void KeyFromLink_ReturnsThePlainWordWhenThereIsNoLink()
    {
        var args = new CommandArgs("Sword rest");
        string? key = args.ExtractKeyFromLink("Hitem", out int index, out _);
        Assert.Equal("Sword", key);
        Assert.Equal(-1, index);
        Assert.Equal("rest", args.Rest);
    }

    [Fact]
    public void KeyFromLink_ReadsQuotedNames_AndLinkKeys()
    {
        Assert.Equal("Worn Shortsword", new CommandArgs("[Worn Shortsword]").ExtractKeyFromLink("Hitem", out _, out _));
        var args = new CommandArgs(StaffLink);
        Assert.Equal("812", args.ExtractKeyFromLink("Hitem", out int index, out _));
        Assert.Equal(0, index);
        Assert.Null(new CommandArgs("|cffffffff|Hspell:133|h[Fireball]|h|r").ExtractKeyFromLink("Hitem", out _, out _));
    }

    [Fact]
    public void Uint32KeyFromLink_AcceptsNumbersAndLinks()
    {
        Assert.True(new CommandArgs("812").ExtractUInt32KeyFromLink("Hitem", out uint plain));
        Assert.Equal(812u, plain);
        Assert.True(new CommandArgs(StaffLink).ExtractUInt32KeyFromLink("Hitem", out uint linked));
        Assert.Equal(812u, linked);
        Assert.False(new CommandArgs("Sword").ExtractUInt32KeyFromLink("Hitem", out _));
    }

    [Fact]
    public void Arg_ReadsQuotedLiteralOrLink()
    {
        Assert.Equal("a b", new CommandArgs("'a b'").ExtractArg());
        Assert.Equal("word", new CommandArgs("word").ExtractArg());
        Assert.Equal(StaffLink, new CommandArgs(StaffLink).ExtractArg());
    }

    [Fact]
    public void OptNotLastArg_ReturnsTheArgOnlyWhenMoreDataFollows()
    {
        var two = new CommandArgs("first second");
        Assert.Equal("first", two.ExtractOptNotLastArg());
        Assert.Equal("second", two.Rest);

        var one = new CommandArgs("only");
        Assert.Null(one.ExtractOptNotLastArg());
        Assert.Equal("only", one.Rest);
    }

    [Fact]
    public void GameTele_AcceptsNumericIdNameAndTeleLink()
    {
        Assert.Equal((12u, (string?)null), Split(new CommandArgs("12")));
        Assert.Equal((0u, (string?)"Stormwind"), Split(new CommandArgs("Stormwind")));
        Assert.Equal((57u, (string?)null), Split(new CommandArgs("|cffffffff|Htele:57|h[Stormwind]|h|r")));

        static (uint, string?) Split(CommandArgs a)
        {
            Assert.True(a.ExtractGameTele(out uint id, out string? name));
            return (id, name);
        }
    }

    [Theory]
    [InlineData("Northshire", "Northshire")]
    [InlineData("northSHIRE", "Northshire")]
    [InlineData("ÉLODIE", "Élodie")]
    public void PlayerName_NormalisesToTitleCase(string raw, string expected)
    {
        Assert.True(PlayerNames.TryNormalize(raw, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Thirteenchars")]
    public void PlayerName_RejectsEmptyAndOverlongNames(string raw)
        => Assert.False(PlayerNames.TryNormalize(raw, out _));

    [Fact]
    public void PlayerTarget_NamedSelectedAndSelfFallbacks()
    {
        var bob = new Target("Bob");
        var sel = new Target("Sel");
        Target? Find(string n) => n == "Bob" ? bob : null;

        // explicit name (case-normalised)
        var named = new CommandArgs("bOB tail");
        Assert.True(PlayerTargetResolver.TryExtract(named, Find, () => sel, out Target? t1, out string n1));
        Assert.Same(bob, t1);
        Assert.Equal("Bob", n1);
        Assert.Equal("tail", named.Rest);

        // no arg -> the selection
        Assert.True(PlayerTargetResolver.TryExtract(new CommandArgs(""), Find, () => sel, out Target? t2, out _));
        Assert.Same(sel, t2);

        // a link works like a name
        Assert.True(PlayerTargetResolver.TryExtract(new CommandArgs("|cffffffff|Hplayer:Bob|h[Bob]|h|r"), Find, () => sel, out Target? t3, out _));
        Assert.Same(bob, t3);

        // selection that is not an online player (the selector answers null) -> failure
        Assert.False(PlayerTargetResolver.TryExtract(new CommandArgs(""), Find, () => null, out Target? t4, out _));
        Assert.Null(t4);

        // unknown name -> failure
        Assert.False(PlayerTargetResolver.TryExtract(new CommandArgs("Nobody"), Find, () => sel, out _, out _));
    }

    private sealed record Target(string Name);

    [Theory]
    [InlineData("1d2h30m10s", 95410u)]
    [InlineData("90s", 90u)]
    [InlineData("2h", 7200u)]
    [InlineData("1x", 0u)]
    [InlineData("", 0u)]
    public void Duration_ParsesLikeTimeStringToSecs(string text, uint expected)
        => Assert.Equal(expected, GmDuration.TimeStringToSecs(text));

    [Theory]
    [InlineData(95410L, false, false, "1 Day 2 Hours 30 Minutes 10 Seconds.")]
    [InlineData(95410L, true, false, "1d2h30m10s")]
    [InlineData(0L, false, false, "0 Second.")]
    [InlineData(3600L, false, false, "1 Hour ")]
    [InlineData(7200L, false, true, "2 Hours ")]
    [InlineData(59L, true, false, "59s")]
    public void Duration_FormatsLikeSecsToTimeString(long secs, bool shortText, bool hoursOnly, string expected)
        => Assert.Equal(expected, GmDuration.SecsToTimeString(secs, shortText, hoursOnly));
}
