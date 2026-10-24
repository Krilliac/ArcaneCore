using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The pager and stable <c>--json</c> output of the debug reads (I4).</summary>
public sealed class GmPagerTests
{
    private enum Mode { Idle, Chasing }

    private static CommandContext Console(List<string> output)
        => CommandContext.ForConsole((WorldSession)RuntimeHelpers.GetUninitializedObject(typeof(WorldSession)), new CommandTable([]), output.Add);

    private static List<GmRow> Rows(int count)
        => [.. Enumerable.Range(1, count).Select(i => new GmRow().Add("n", i).Add("name", "row" + i))];

    [Fact]
    public void ThePageSizeIsTheInspectCap() => Assert.Equal(InspectCommands.MaxRows, GmPager.RowsPerPage);

    [Theory]
    [InlineData("", "", 1, false)]
    [InlineData("  ", "  ", 1, false)]
    [InlineData("foo bar", "foo bar", 1, false)]
    [InlineData("page #3", "", 3, false)]
    [InlineData("page 3", "", 3, false)]
    [InlineData("PAGE #12", "", 12, false)]
    [InlineData("--json", "", 1, true)]
    [InlineData("--JSON page #2", "", 2, true)]
    [InlineData("foo --json bar", "foo --json bar", 1, false)]
    [InlineData("foo --json", "foo", 1, true)]
    [InlineData("foo page #2 --json", "foo", 2, true)]
    [InlineData("foo --json page #2", "foo", 2, true)]
    [InlineData("the --json flag is --json", "the --json flag is", 1, true)]
    [InlineData("wolf  page   #2 --json", "wolf", 2, true)]
    [InlineData("page #2 wolf", "page #2 wolf", 1, false)]
    public void TryParseStripsTheFlagAndTheTrailingPage(string args, string rest, int page, bool json)
    {
        Assert.True(GmPager.TryParse(args, out string actualRest, out GmOutputOptions options, out string error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(rest, actualRest);
        Assert.Equal(new GmOutputOptions(page, json), options);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("wolf page")]
    [InlineData("page #0")]
    [InlineData("page #x")]
    [InlineData("page #-1")]
    [InlineData("page #99999999999")]
    public void TryParseRefusesABadPage(string args)
    {
        Assert.False(GmPager.TryParse(args, out _, out _, out string error));
        Assert.Equal(GmPager.PageNeedsNumber, error);
    }

    [Fact]
    public void TheContextOverloadAnswersABadPageAndFailsTheCommand()
    {
        var output = new List<string>();
        CommandContext context = Console(output);

        Assert.False(GmPager.TryParse(context, "page #0", out _, out _));

        Assert.Equal([GmPager.PageNeedsNumber], output);
        Assert.Equal("bad-page", context.Failure);

        output.Clear();
        Assert.False(GmPager.TryParse(context, "page --json", out _, out _));
        JsonNode error = JsonNode.Parse(Assert.Single(output))!;
        Assert.Equal("bad-page", (string?)error["error"]);
    }

    [Fact]
    public void ARowPrintsItsFieldsOrItsText()
    {
        GmRow fields = new GmRow().Add("guid", 12u).Add("name", "Wolf").Add("alive", true).Add("hp", 1.5f).Add("target", null);
        Assert.Equal("guid=12 name=Wolf alive=true hp=1.5 target=none", fields.ToLine());

        GmRow text = new GmRow().Add("guid", 12u).WithText("Wolf (12)");
        Assert.Equal("Wolf (12)", text.ToLine());
        Assert.Equal(12u, text.Fields[0].Value);
    }

    [Fact]
    public void AListPrintsTheFirstPageAndNamesTheNext()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", Rows(60), new GmOutputOptions(1, false));

        Assert.Equal(1 + 25 + 1, output.Count);
        Assert.Equal("Bots: 60", output[0]);
        Assert.Equal("n=1 name=row1", output[1]);
        Assert.Equal("n=25 name=row25", output[25]);
        Assert.Equal("Page 1/3 (rows 1-25 of 60). Add 'page #2' for more.", output[26]);
    }

    [Fact]
    public void TheLastPagePrintsTheRestWithoutAMoreHint()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", Rows(60), new GmOutputOptions(3, false));

        Assert.Equal(1 + 10 + 1, output.Count);
        Assert.Equal("n=51 name=row51", output[1]);
        Assert.Equal("n=60 name=row60", output[10]);
        Assert.Equal("Page 3/3 (rows 51-60 of 60).", output[11]);
    }

    [Fact]
    public void AShortListHasNoFooter()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", Rows(25), new GmOutputOptions(1, false));
        Assert.Equal(26, output.Count);

        output.Clear();
        GmPager.Reply(Console(output), "Bots", [], new GmOutputOptions(1, false));
        Assert.Equal(["Bots: 0"], output);
    }

    [Fact]
    public void APagePastTheEndAnswersAnErrorAndFailsTheCommand()
    {
        var output = new List<string>();
        CommandContext context = Console(output);

        GmPager.Reply(context, "Bots", Rows(26), new GmOutputOptions(3, false));

        Assert.Equal(["Page 3 does not exist; there are 2 pages."], output);
        Assert.Equal("page-out-of-range", context.Failure);

        output.Clear();
        GmPager.Reply(Console(output), "Bots", [], new GmOutputOptions(2, false));
        Assert.Equal(["Page 2 does not exist; there is 1 page."], output);
    }

    [Fact]
    public void TheJsonListIsJsonLinesAHeaderThenOneObjectPerRow()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", Rows(60), new GmOutputOptions(2, true));

        Assert.Equal(1 + 25, output.Count);
        Assert.Equal("{\"command\":\"Bots\",\"page\":2,\"pages\":3,\"rows\":25}", output[0]);
        Assert.Equal("{\"n\":26,\"name\":\"row26\"}", output[1]);
        Assert.Equal("{\"n\":50,\"name\":\"row50\"}", output[25]);
        Assert.All(output, line => Assert.DoesNotContain('\n', line));
        Assert.All(output, line => Assert.NotNull(JsonNode.Parse(line)));
    }

    [Fact]
    public void TheJsonHeaderCountsTheRowsOfTheLastPage()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", Rows(60), new GmOutputOptions(3, true));

        Assert.Equal("{\"command\":\"Bots\",\"page\":3,\"pages\":3,\"rows\":10}", output[0]);
        Assert.Equal(11, output.Count);
    }

    [Fact]
    public void TheJsonListOfNothingIsJustAHeader()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", [], new GmOutputOptions(1, true));
        Assert.Equal(["{\"command\":\"Bots\",\"page\":1,\"pages\":1,\"rows\":0}"], output);
    }

    [Fact]
    public void JsonFieldsKeepTheirTypes()
    {
        var output = new List<string>();
        GmRow row = new GmRow()
            .Add("s", "a \"quoted\" <b>").Add("i", -5).Add("u", uint.MaxValue).Add("l", long.MinValue).Add("ul", ulong.MaxValue)
            .Add("f", 0.5f).Add("d", double.NaN).Add("inf", float.PositiveInfinity).Add("b", false).Add("n", null)
            .Add("e", Mode.Chasing).Add("g", Guid.Empty).Add("other", new Version(1, 2));

        GmPager.ReplyRow(Console(output), "One", row, new GmOutputOptions(1, true));

        Assert.Equal(2, output.Count);
        Assert.Equal("{\"command\":\"One\",\"page\":1,\"pages\":1,\"rows\":1}", output[0]);
        string line = output[1];
        JsonNode r = JsonNode.Parse(line)!;
        Assert.Equal("a \"quoted\" <b>", (string?)r["s"]);
        Assert.Equal(-5, (int)r["i"]!);
        Assert.Equal(uint.MaxValue, (uint)r["u"]!);
        Assert.Equal(long.MinValue, (long)r["l"]!);
        Assert.Equal(ulong.MaxValue, (ulong)r["ul"]!);
        Assert.Equal(0.5, (double)r["f"]!);
        Assert.Null(r["d"]);
        Assert.Null(r["inf"]);
        Assert.False((bool)r["b"]!);
        Assert.Null(r["n"]);
        Assert.Equal("Chasing", (string?)r["e"]);
        Assert.Equal(Guid.Empty.ToString(), (string?)r["g"]);
        Assert.Equal("1.2", (string?)r["other"]);
        Assert.DoesNotContain('\n', line);
        Assert.Equal(new[] { "s", "i", "u", "l", "ul", "f", "d", "inf", "b", "n", "e", "g", "other" }, r.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void ASingleRowPrintsItsLineInTextMode()
    {
        var output = new List<string>();
        GmPager.ReplyRow(Console(output), "One", new GmRow().Add("a", 1).WithText("custom"), GmOutputOptions.Default);
        Assert.Equal(["custom"], output);
    }

    [Fact]
    public void ThePageBoundaryIs25Rows()
    {
        var output = new List<string>();
        GmPager.Reply(Console(output), "Bots", Rows(25), new GmOutputOptions(2, false));
        Assert.Equal(["Page 2 does not exist; there is 1 page."], output);

        output.Clear();
        GmPager.Reply(Console(output), "Bots", Rows(26), new GmOutputOptions(2, false));
        Assert.Equal(["Bots: 26", "n=26 name=row26", "Page 2/2 (rows 26-26 of 26)."], output);

        output.Clear();
        GmPager.Reply(Console(output), "Bots", Rows(26), new GmOutputOptions(1, true));
        Assert.Equal(26, output.Count);
        Assert.Equal("{\"command\":\"Bots\",\"page\":1,\"pages\":2,\"rows\":25}", output[0]);
    }

    [Fact]
    public void AJsonPageOutOfRangeIsAnErrorObjectWithThePageCount()
    {
        var output = new List<string>();
        CommandContext context = Console(output);

        GmPager.Reply(context, "Bots", Rows(26), new GmOutputOptions(9, true));

        Assert.Equal(["{\"error\":\"page-out-of-range\",\"message\":\"Page 9 does not exist; there are 2 pages.\",\"pages\":2}"], output);
        Assert.Equal("page-out-of-range", context.Failure);
    }

    [Fact]
    public void JsonIgnoresTheTextOverrideAndTextIgnoresNothingInTheFields()
    {
        var output = new List<string>();
        GmRow row = new GmRow().Add("a", 1).Add("b", "x").WithText("custom line");

        GmPager.Reply(Console(output), "T", [row], new GmOutputOptions(1, true));
        Assert.Equal(["{\"command\":\"T\",\"page\":1,\"pages\":1,\"rows\":1}", "{\"a\":1,\"b\":\"x\"}"], output);

        output.Clear();
        GmPager.ReplyRow(Console(output), "T", row, new GmOutputOptions(1, true));
        Assert.Equal(["{\"command\":\"T\",\"page\":1,\"pages\":1,\"rows\":1}", "{\"a\":1,\"b\":\"x\"}"], output);
    }

    [Fact]
    public void AGuidIsWrittenAsAHexStringInBothFormats()
    {
        var output = new List<string>();
        GmRow row = new GmRow().Add("guid", new ObjectGuid(0xF130_0001_2B00_0001UL));

        Assert.Equal("guid=0xF13000012B000001", row.ToLine());
        GmPager.ReplyRow(Console(output), "G", row, new GmOutputOptions(1, true));
        Assert.Equal("{\"guid\":\"0xF13000012B000001\"}", output[1]);
    }

    [Fact]
    public void ARowRefusesABlankKey()
    {
        Assert.Throws<ArgumentException>(() => new GmRow().Add(" ", 1));
        Assert.Throws<ArgumentNullException>(() => new GmRow().Add(null!, 1));
    }

    [Fact]
    public void ABadPageErrorIsJsonOnlyWhenTheFlagWasGivenAtTheEnd()
    {
        var output = new List<string>();
        Assert.False(GmPager.TryParse(Console(output), "wolf page #x --json", out _, out _));
        Assert.Equal("{\"error\":\"bad-page\",\"message\":\"" + GmPager.PageNeedsNumber + "\"}", Assert.Single(output));

        output.Clear();
        Assert.False(GmPager.TryParse(Console(output), "wolf page #x", out _, out _));
        Assert.Equal([GmPager.PageNeedsNumber], output);
    }

    [Fact]
    public void ANullArgumentIsNoOptions()
    {
        Assert.True(GmPager.TryParse(null, out string rest, out GmOutputOptions options, out _));
        Assert.Equal(string.Empty, rest);
        Assert.Equal(GmOutputOptions.Default, options);
    }

    [Fact]
    public void AJsonErrorCarriesTheCodeAndMessage()
    {
        var output = new List<string>();
        CommandContext context = Console(output);

        GmPager.ReplyError(context, json: true, "no-target", "nothing there", pages: 2);

        Assert.Equal(["{\"error\":\"no-target\",\"message\":\"nothing there\",\"pages\":2}"], output);
        Assert.Equal("no-target", context.Failure);
    }
}
