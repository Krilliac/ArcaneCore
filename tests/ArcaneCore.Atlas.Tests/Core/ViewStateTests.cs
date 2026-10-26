using System.Reflection;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class ViewStateTests
{
    /// <summary>RFC 7396 Appendix A, every example test case (original, patch, result).</summary>
    public static TheoryData<string, string, string> Rfc7396Cases => new()
    {
        { """{"a":"b"}""", """{"a":"c"}""", """{"a":"c"}""" },
        { """{"a":"b"}""", """{"b":"c"}""", """{"a":"b","b":"c"}""" },
        { """{"a":"b"}""", """{"a":null}""", """{}""" },
        { """{"a":"b","b":"c"}""", """{"a":null}""", """{"b":"c"}""" },
        { """{"a":["b"]}""", """{"a":"c"}""", """{"a":"c"}""" },
        { """{"a":"c"}""", """{"a":["b"]}""", """{"a":["b"]}""" },
        { """{"a":{"b":"c"}}""", """{"a":{"b":"d","c":null}}""", """{"a":{"b":"d"}}""" },
        { """{"a":[{"b":"c"}]}""", """{"a":[1]}""", """{"a":[1]}""" },
        { """["a","b"]""", """["c","d"]""", """["c","d"]""" },
        { """{"a":"b"}""", """["c"]""", """["c"]""" },
        { """{"a":"foo"}""", "null", "null" },
        { """{"a":"foo"}""", "\"bar\"", "\"bar\"" },
        { """{"e":null}""", """{"a":1}""", """{"e":null,"a":1}""" },
        { "[1,2]", """{"a":"b","c":null}""", """{"a":"b"}""" },
        { "{}", """{"a":{"bb":{"ccc":null}}}""", """{"a":{"bb":{}}}""" },
    };

    [Theory]
    [MemberData(nameof(Rfc7396Cases))]
    public void MergePatch_MatchesRfc7396(string original, string patch, string expected)
    {
        JsonNode? result = ViewState.MergePatch(JsonNode.Parse(original), JsonNode.Parse(patch));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), result), $"got {result?.ToJsonString() ?? "null"}");
    }

    [Fact]
    public void Default_RoundTripsThroughJson()
    {
        string json = ViewState.Default.ToJson().ToJsonString();
        ViewState parsed = ViewState.FromJson(JsonNode.Parse(json)!.AsObject());

        Assert.Equal(json, parsed.ToJson().ToJsonString());
        Assert.Equal(LayerCatalog.All.Count, parsed.Layers.Count);
    }

    [Fact]
    public void Apply_ChangesOnlyWhatThePatchNames()
    {
        ViewState next = ViewState.Default.Apply(JsonNode.Parse(
            """{"map":1,"camera":{"yaw":-90,"mode":"top"},"layers":{"terrain":{"visible":false,"color":"#00ff00"}},"compare":"split"}""")!.AsObject());

        Assert.Equal(1u, next.MapId);
        Assert.Equal(270, next.Camera.Yaw);
        Assert.Equal(CameraMode.Top, next.Camera.Mode);
        Assert.Equal(ViewState.Default.Camera.Target, next.Camera.Target);
        Assert.False(next.Layers[LayerIds.Terrain].Visible);
        Assert.Equal("#00FF00", next.Layers[LayerIds.Terrain].Color);
        Assert.Equal(ViewState.Default.Layers[LayerIds.Liquid], next.Layers[LayerIds.Liquid]);
        Assert.Equal(CompareMode.Split, next.Compare);
    }

    [Fact]
    public void Apply_AcceptsPatchesBuiltInCode()
    {
        // ToJson builds its numbers from CLR values (uint map, int radius); applying a code-built patch on top of it
        // is what every command handler does.
        ViewState next = ViewState.Default.Apply(new JsonObject
        {
            ["loadRadius"] = 2,
            ["camera"] = new JsonObject { ["distance"] = 300f, ["target"] = new JsonArray(1, 2L, 3.5m) },
        });

        Assert.Equal(2, next.LoadRadiusTiles);
        Assert.Equal(300, next.Camera.Distance);
        Assert.Equal(new WorldPoint(1, 2, 3.5), next.Camera.Target);
        Assert.Equal(0u, next.MapId);
    }

    [Theory]
    [InlineData("""{"layers":{"nope":{"visible":true}}}""")]
    [InlineData("""{"layers":{"terrain":{"color":"red"}}}""")]
    [InlineData("""{"layers":{"terrain":{"opacity":2}}}""")]
    [InlineData("""{"camera":{"distance":0}}""")]
    [InlineData("""{"camera":{"pitch":90}}""")]
    [InlineData("""{"camera":{"target":[1,2]}}""")]
    [InlineData("""{"camera":{"mode":"sideways"}}""")]
    [InlineData("""{"loadRadius":1.5}""")]
    [InlineData("""{"loadRadius":9}""")]
    [InlineData("""{"follow":"not-hex"}""")]
    [InlineData("""{"map":null}""")]
    public void Apply_RefusesInvalidViews(string patch)
    {
        CommandException e = Assert.Throws<CommandException>(() => ViewState.Default.Apply(JsonNode.Parse(patch)!.AsObject()));

        Assert.Equal(CommandException.BadArguments, e.Code);
    }

    [Fact]
    public void Store_VersionsEveryChange_AndKeepsTheOldViewOnRefusal()
    {
        var store = new ViewStore(ViewState.Default);
        var seen = new List<(long Version, string Origin)>();
        store.Changed += (_, version, origin) => seen.Add((version, origin));

        store.Patch(JsonNode.Parse("""{"follow":"F130000000000123"}""")!.AsObject(), "mcp");
        Assert.Throws<CommandException>(() => store.Patch(JsonNode.Parse("""{"split":7}""")!.AsObject(), "ui"));
        store.Patch(JsonNode.Parse("""{"follow":null}""")!.AsObject(), "ui");

        Assert.Equal(new[] { (1L, "mcp"), (2L, "ui") }, seen);
        Assert.Equal(2, store.Version);
        Assert.Null(store.Current.FollowGuid);
    }

    [Fact]
    public void Store_PublishesEveryVersionInOrder_WhenManyThreadsPatchAtOnce()
    {
        const int Threads = 8;
        const int PerThread = 150;
        var store = new ViewStore(ViewState.Default);
        var applied = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var changed = new System.Collections.Concurrent.ConcurrentQueue<long>();
        store.Applied += (_, _, version, _, _) =>
        {
            applied.Enqueue(version);
            Thread.Yield();
        };
        store.Changed += (_, version, _) => changed.Enqueue(version);

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                store.Patch(new JsonObject { ["camera"] = new JsonObject { ["yaw"] = (t * PerThread) + i } }, $"ui:{t}");
            }
        });

        long[] expected = [.. Enumerable.Range(1, Threads * PerThread).Select(v => (long)v)];
        Assert.Equal(expected, applied.ToArray());
        Assert.Equal(expected, changed.ToArray());
    }

    [Fact]
    public void Store_AUiCannotClearAFollowAnotherUiOwns_ButTheOwnerAndNonUiOriginsCan()
    {
        var store = new ViewStore(ViewState.Default);
        JsonObject Clear(double? yaw = null)
        {
            var patch = new JsonObject { ["follow"] = null };
            if (yaw is { } y)
            {
                patch["camera"] = new JsonObject { ["yaw"] = y };
            }

            return patch;
        }

        store.Patch(new JsonObject { ["follow"] = "AA" }, "ui:a");
        store.Patch(new JsonObject { ["follow"] = "BB" }, "ui:b");

        store.Patch(Clear(yaw: 12), "ui:a");

        Assert.Equal("BB", store.Current.FollowGuid);
        Assert.Equal("ui:b", store.FollowOrigin);
        Assert.Equal(12, store.Current.Camera.Yaw);

        store.Patch(Clear(), "ui:b");
        Assert.Null(store.Current.FollowGuid);

        store.Patch(new JsonObject { ["follow"] = "CC" }, "ui:b");
        store.Patch(Clear(), "cli");
        Assert.Null(store.Current.FollowGuid);

        store.Patch(new JsonObject { ["follow"] = "DD" }, "cli");
        store.Patch(Clear(), "ui:a");
        Assert.Null(store.Current.FollowGuid);
    }

    [Fact]
    public void Store_Snapshot_ReportsTheViewVersionAndFollowOwnerOfOneMoment()
    {
        var store = new ViewStore(ViewState.Default);
        (ViewState view, long version, string? owner) = store.Snapshot();
        Assert.Equal((0L, (string?)null), (version, owner));
        Assert.Same(store.Current, view);

        store.Patch(new JsonObject { ["follow"] = "AA" }, "ui:a");
        (view, version, owner) = store.Snapshot();
        Assert.Equal("AA", view.FollowGuid);
        Assert.Equal(1, version);
        Assert.Equal("ui:a", owner);

        store.Patch(new JsonObject { ["follow"] = null }, "ui:a");
        Assert.Null(store.Snapshot().FollowOrigin);
    }

    [Fact]
    public void Catalog_IdsAreUnique_ColoursValid_AndEveryIdConstantIsListed()
    {
        Assert.Equal(LayerCatalog.All.Count, LayerCatalog.All.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (LayerInfo layer in LayerCatalog.All)
        {
            Assert.True(ViewState.IsColor(layer.DefaultColor), $"{layer.Id} {layer.DefaultColor}");
            foreach ((string key, string colour) in layer.AttributeColors ?? new Dictionary<string, string>())
            {
                Assert.True(ViewState.IsColor(colour), $"{layer.Id} {key} {colour}");
            }

            Assert.Equal(layer.View == LayerView.Live, !layer.Tiled);
        }

        string[] constants = typeof(LayerIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(constants);
        Assert.All(constants, id => Assert.True(LayerCatalog.TryGet(id, out _), id));
        Assert.Equal(constants.Length, LayerCatalog.All.Count);
    }
}
