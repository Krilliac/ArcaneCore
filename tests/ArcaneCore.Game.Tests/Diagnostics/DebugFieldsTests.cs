using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game.Diagnostics;
using Xunit;

namespace ArcaneCore.Game.Tests.Diagnostics;

public sealed class DebugFieldsTests
{
    private enum Mode
    {
        Idle,
        Chase,
    }

    private sealed class Inner : IDebugDescribable
    {
        public void Describe(DebugWriter writer) => writer.Add("hp", 42).Add("alive", true);
    }

    private sealed class Outer : IDebugDescribable
    {
        public void Describe(DebugWriter writer)
        {
            writer.Add("name", "Boar").AddEnum("mode", Mode.Chase);
            writer.AddObject("combat", new Inner());
            writer.Add("after", 1);
        }
    }

    [Fact]
    public void Snapshot_CollectsFieldsInOrder_WithSections()
    {
        DebugWriter writer = DebugFields.Snapshot(new Outer());

        Assert.Equal(
            [new DebugField("", "name", "Boar"), new DebugField("", "mode", "Chase"), new DebugField("combat", "hp", "42"), new DebugField("combat", "alive", "true"), new DebugField("", "after", "1")],
            writer.Fields);
        Assert.False(writer.Truncated);
    }

    [Fact]
    public void ToLines_PutsASectionHeaderWhereTheSectionChanges()
    {
        IReadOnlyList<string> lines = DebugFields.Snapshot(new Outer()).ToLines();

        Assert.Equal(["name: Boar", "mode: Chase", "[combat]", "hp: 42", "alive: true", "[]", "after: 1"], lines);
    }

    [Fact]
    public void Values_AreFormattedCompactly()
    {
        var writer = new DebugWriter();
        writer.Add("speed", 7.5f).Add("big", 1234567L).AddPosition("pos", 1.5f, -2.25f, 3f).Add("dt", TimeSpan.FromMilliseconds(12.5)).Add("none", (string?)null);

        Assert.Equal(["speed: 7.5", "big: 1234567", "pos: 1.5 -2.25 3", "dt: 12.5ms", "none: -"], writer.ToLines());
    }

    [Fact]
    public void AddList_ShowsTheCountAndAtMostMaxItems()
    {
        var writer = new DebugWriter();
        writer.AddList("few", new List<int> { 1, 2 }, 8).AddList("many", new List<int> { 1, 2, 3, 4 }, 2).AddList("empty", new List<int>());

        Assert.Equal(["few: 2 [1, 2]", "many: 4 [1, 2, ...]", "empty: 0"], writer.ToLines());
    }

    [Fact]
    public void Writer_StopsAtTheCap_AndSaysSo()
    {
        var writer = new DebugWriter(3);
        for (int i = 0; i < 10; i++)
        {
            writer.Add("f" + i, i);
        }

        Assert.Equal(3, writer.Fields.Count);
        Assert.True(writer.Truncated);
        Assert.Equal("(output capped at 3 fields)", writer.ToLines()[^1]);
    }

    [Fact]
    public void ToJson_NestsSections_AndMarksTruncation()
    {
        var writer = new DebugWriter(3);
        writer.Add("name", "Boar").AddObject("combat", new Inner());
        writer.Add("over", 1);

        using JsonDocument doc = JsonDocument.Parse(writer.ToJson());
        JsonElement root = doc.RootElement;

        Assert.Equal("Boar", root.GetProperty("name").GetString());
        Assert.Equal("42", root.GetProperty("combat").GetProperty("hp").GetString());
        Assert.Equal("true", root.GetProperty("combat").GetProperty("alive").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void AddObject_WithNull_WritesADash_AndRestoresTheSection()
    {
        var writer = new DebugWriter();
        writer.Section("top").AddObject("child", null).Add("x", 1);

        Assert.Equal([new DebugField("child", "value", "-"), new DebugField("top", "x", "1")], writer.Fields);
    }

    [Fact]
    public void EveryNumericOverload_FormatsPlainly()
    {
        var writer = new DebugWriter();
        writer.Add("u", uint.MaxValue).Add("ul", ulong.MaxValue).Add("neg", -5).Add("d", 2.5d).Add("f", 1.23456f).Add("no", false).Add("zero", TimeSpan.Zero);

        Assert.Equal(["u: 4294967295", "ul: 18446744073709551615", "neg: -5", "d: 2.5", "f: 1.235", "no: false", "zero: 0ms"], writer.ToLines());
    }

    [Fact]
    public void Guids_AndEnums_AreWrittenByTheirOwnText()
    {
        var guid = new ObjectGuid(0x1234);
        var writer = new DebugWriter();
        writer.Add("who", guid).AddEnum("mode", Mode.Idle);

        Assert.Equal(["who: " + guid, "mode: Idle"], writer.ToLines());
    }

    [Fact]
    public void Formatting_IgnoresTheCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone(); // the test host runs globalization-invariant, so a named culture is unavailable
            comma.NumberFormat.NumberDecimalSeparator = ",";
            comma.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("1,5", 1.5f.ToString("0.###", CultureInfo.CurrentCulture));
            var writer = new DebugWriter();
            writer.Add("f", 1.5f).Add("d", 1234.5d).Add("n", 1234567).AddPosition("p", 1.5f, 2.5f, 3.5f).AddList("l", new List<double> { 1.5 });

            Assert.Equal(["f: 1.5", "d: 1234.5", "n: 1234567", "p: 1.5 2.5 3.5", "l: 1 [1.5]"], writer.ToLines());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AddList_AtExactlyMaxItems_HasNoEllipsis_AndZeroMaxItemsShowsOnlyTheCount()
    {
        var writer = new DebugWriter();
        writer.AddList("exact", new List<int> { 1, 2, 3 }, 3).AddList("countOnly", new List<int> { 1, 2, 3 }, 0).AddList("words", new List<string> { "a", "b" });

        Assert.Equal(["exact: 3 [1, 2, 3]", "countOnly: 3", "words: 2 [a, b]"], writer.ToLines());
        Assert.Throws<ArgumentNullException>(() => writer.AddList<int>("x", null!));
    }

    [Fact]
    public void TheCap_IsExact_ExactlyMaxFieldsIsNotTruncated_OneMoreIs()
    {
        var writer = new DebugWriter(2);
        writer.Add("a", 1).Add("b", 2);
        Assert.False(writer.Truncated);
        Assert.Equal(["a: 1", "b: 2"], writer.ToLines());

        writer.Add("c", 3);

        Assert.True(writer.Truncated);
        Assert.Equal(2, writer.Fields.Count);
    }

    [Fact]
    public void ANestedObject_ThatRunsPastTheCap_TruncatesTheWholeWriter_AndTheSectionIsRestored()
    {
        var writer = new DebugWriter(2);

        writer.Section("top").AddObject("child", new Inner()).Add("after", 1);

        Assert.Equal([new DebugField("child", "hp", "42"), new DebugField("child", "alive", "true")], writer.Fields);
        Assert.True(writer.Truncated);
        Assert.Equal("(output capped at 2 fields)", writer.ToLines()[^1]);
    }

    [Fact]
    public void Section_EmptyReturnsToTheTopLevel_AndNullIsRejected()
    {
        var writer = new DebugWriter();
        writer.Section("s").Add("in", 1).Section("").Add("out", 2);

        Assert.Equal([new DebugField("s", "in", "1"), new DebugField("", "out", "2")], writer.Fields);
        Assert.Equal(["[s]", "in: 1", "[]", "out: 2"], writer.ToLines());
        Assert.Throws<ArgumentNullException>(() => writer.Section(null!));
        Assert.Throws<ArgumentNullException>(() => writer.AddObject(null!, new Inner()));
    }

    [Fact]
    public void ToJson_OfAnEmptyWriter_IsAnEmptyObject_AndEscapesSpecialCharacters()
    {
        Assert.Equal("{}", new DebugWriter().ToJson());

        var writer = new DebugWriter();
        writer.Add("quote", "say \"hi\"\nnow");
        using JsonDocument doc = JsonDocument.Parse(writer.ToJson());

        Assert.Equal("say \"hi\"\nnow", doc.RootElement.GetProperty("quote").GetString());
        Assert.False(doc.RootElement.TryGetProperty("truncated", out _));
    }

    [Fact]
    public void ToJson_GroupsASectionThatIsWrittenInTwoPlaces()
    {
        var writer = new DebugWriter();
        writer.Section("a").Add("x", 1).Section("b").Add("y", 2).Section("a").Add("z", 3);

        using JsonDocument doc = JsonDocument.Parse(writer.ToJson());
        JsonElement a = doc.RootElement.GetProperty("a");

        Assert.Equal("1", a.GetProperty("x").GetString());
        Assert.Equal("3", a.GetProperty("z").GetString());
        Assert.Equal("2", doc.RootElement.GetProperty("b").GetProperty("y").GetString());
    }

    [Fact]
    public void Snapshot_HonoursTheCap_AndRejectsNull()
    {
        DebugWriter writer = DebugFields.Snapshot(new Outer(), 2);

        Assert.Equal(2, writer.Fields.Count);
        Assert.True(writer.Truncated);
        Assert.Throws<ArgumentNullException>(() => DebugFields.Snapshot(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => DebugFields.Snapshot(new Outer(), 0));
    }

    [Fact]
    public void Writer_RejectsAnEmptyNameAndANonPositiveCap()
    {
        Assert.Throws<ArgumentException>(() => new DebugWriter().Add("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DebugWriter(0));
    }
    private sealed class Cyclic : IDebugDescribable
    {
        public void Describe(DebugWriter writer)
        {
            writer.Add("tick", 1);
            writer.AddObject("self", this);
        }
    }

    [Fact]
    public void ACyclicDescribe_StopsAtTheDepthLimit_InsteadOfOverflowingTheStack()
    {
        DebugWriter writer = DebugFields.Snapshot(new Cyclic());

        Assert.True(writer.Truncated);
        Assert.Equal(DebugWriter.MaxDepth + 1, writer.Fields.Count);
        Assert.Contains("\"truncated\":true", writer.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_NeverRepeatsAPropertyName()
    {
        var writer = new DebugWriter();
        writer.Add("hp", 1).Add("hp", 2).Add("truncated", "x").Add("combat", "top");
        writer.Section("combat").Add("a", 1).Add("a", 2);
        writer.Section("truncated").Add("b", 1);

        using JsonDocument doc = JsonDocument.Parse(writer.ToJson()); // a repeated name would still parse, so list the names
        string[] top = [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];

        Assert.Equal(top.Length, top.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["hp", "hp_2", "truncated_2", "combat", "combat_2", "truncated_3"], top);
        Assert.Equal(["a", "a_2"], doc.RootElement.GetProperty("combat_2").EnumerateObject().Select(p => p.Name));
    }

    private sealed class Throwing : IDebugDescribable
    {
        public void Describe(DebugWriter writer)
        {
            writer.Add("before", 1);
            throw new InvalidOperationException("describe failed");
        }
    }

    [Fact]
    public void ADescribeThatThrows_LeavesTheWriterInItsOwnSectionAndDepth()
    {
        var writer = new DebugWriter();
        writer.Section("top");

        Assert.Throws<InvalidOperationException>(() => writer.AddObject("child", new Throwing()));
        writer.Add("after", 1);
        for (int i = 0; i < DebugWriter.MaxDepth; i++)
        {
            writer.AddObject("again", new Inner()); // a leaked depth would start truncating after a few of these
        }

        Assert.False(writer.Truncated);
        Assert.Equal(new DebugField("top", "after", "1"), writer.Fields[1]);
    }

    [Fact]
    public void ToLines_MarksATopLevelFieldThatFollowsASection_WithAnEmptyHeader()
    {
        var writer = new DebugWriter();
        writer.Add("first", 1).Section("s").Add("in", 2).Section("").Add("last", 3);

        Assert.Equal(["first: 1", "[s]", "in: 2", "[]", "last: 3"], writer.ToLines());
    }
}
