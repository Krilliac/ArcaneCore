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

        Assert.Equal(["name: Boar", "mode: Chase", "[combat]", "hp: 42", "alive: true", "after: 1"], lines);
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
    public void Writer_RejectsAnEmptyNameAndANonPositiveCap()
    {
        Assert.Throws<ArgumentException>(() => new DebugWriter().Add("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DebugWriter(0));
    }
}
