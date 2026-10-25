using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Tests.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

/// <summary>
/// What lane A1 adds to the shared catalog and view (design section 9): three reserved, unsourced client layers
/// (<c>ref.terrain.tex</c>, <c>ref.wmo.tex</c>, <c>ref.m2.tex</c>, default hidden) and the two texture view fields. <c>live.models</c>
/// is not A1's: it lands with the live module in lane A4, so the catalog must stay at 35 layers here.
/// </summary>
public sealed class ModelFoundationCatalogTests
{
    private static readonly string[] Reserved = ["ref.terrain.tex", "ref.wmo.tex", "ref.m2.tex"];

    [Fact]
    public void TheThreeTexturedLayers_AreReserved_InTheClientReferenceGroup_HiddenAndTiled()
    {
        Assert.Equal("ref.terrain.tex", LayerIds.RefTerrainTex);
        Assert.Equal("ref.wmo.tex", LayerIds.RefWmoTex);
        Assert.Equal("ref.m2.tex", LayerIds.RefM2Tex);
        foreach (string id in Reserved)
        {
            Assert.True(LayerCatalog.TryGet(id, out LayerInfo info), id);
            Assert.Equal(LayerGroup.Reference, info.Group);
            Assert.Equal(LayerView.Client, info.View);
            Assert.False(info.DefaultVisible);
            Assert.True(info.Tiled);
            Assert.False(string.IsNullOrWhiteSpace(info.Title));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
            Assert.True(ViewState.IsColor(info.DefaultColor));
        }
    }

    [Fact]
    public void TheCatalog_HasThirtyFiveLayers_AndNoLiveModelsYet()
    {
        Assert.Equal(35, LayerCatalog.All.Count);
        Assert.Equal(LayerCatalog.All.Count, LayerCatalog.All.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.False(LayerCatalog.TryGet("live.models", out _));
        Assert.Equal(Reserved, LayerCatalog.All.Select(l => l.Id).Where(Reserved.Contains).ToArray());
    }

    [Fact]
    public void TheDefaultView_ListsTheNewLayers_Hidden()
    {
        foreach (string id in Reserved)
        {
            Assert.False(ViewState.Default.Layers[id].Visible);
        }
    }

    [Fact]
    public async Task LayersList_ReportsThemWithoutASource_UntilALaneAddsOne()
    {
        await using HostRig rig = HostRig.Create();

        JsonNode data = await rig.DataAsync("layers.list");

        foreach (string id in Reserved)
        {
            JsonNode entry = data.AsArray().Single(l => l!["id"]!.GetValue<string>() == id)!;
            Assert.False(entry["hasSource"]!.GetValue<bool>());
            Assert.Equal("Client", entry["view"]!.GetValue<string>(), ignoreCase: true);
            Assert.Equal("Reference", entry["group"]!.GetValue<string>(), ignoreCase: true);
            Assert.False(entry["defaultVisible"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void TextureView_DefaultsToRadiusOneAnd384Mb()
    {
        Assert.Equal(1, ViewState.Default.TextureRadius);
        Assert.Equal(384, ViewState.Default.TextureBudgetMb);
        JsonObject json = ViewState.Default.ToJson();
        Assert.Equal(1, json["textureRadius"]!.GetValue<int>());
        Assert.Equal(384, json["textureBudgetMb"]!.GetValue<int>());
    }

    [Fact]
    public void TextureView_RoundTripsThroughJson()
    {
        ViewState changed = ViewState.Default.Apply(new JsonObject { ["textureRadius"] = 2, ["textureBudgetMb"] = 1024 });

        Assert.Equal(2, changed.TextureRadius);
        Assert.Equal(1024, changed.TextureBudgetMb);
        ViewState back = ViewState.FromJson(changed.ToJson());
        Assert.Equal(2, back.TextureRadius);
        Assert.Equal(1024, back.TextureBudgetMb);
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(2, 2048)]
    [InlineData(1, 64)]
    [InlineData(1, 2048)]
    public void TextureView_AcceptsItsBoundaries(int radius, int budget)
    {
        ViewState view = ViewState.Default.Apply(new JsonObject { ["textureRadius"] = radius, ["textureBudgetMb"] = budget });

        Assert.Equal(radius, view.TextureRadius);
        Assert.Equal(budget, view.TextureBudgetMb);
    }

    [Theory]
    [InlineData("textureRadius", -1)]
    [InlineData("textureRadius", 3)]
    [InlineData("textureBudgetMb", 63)]
    [InlineData("textureBudgetMb", 2049)]
    [InlineData("textureBudgetMb", 0)]
    public void TextureView_RefusesOutOfRangeValues(string field, int value)
    {
        CommandException error = Assert.Throws<CommandException>(() => ViewState.Default.Apply(new JsonObject { [field] = value }));

        Assert.Equal(CommandException.BadArguments, error.Code);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TextureView_RefusesFractionsAndStrings()
    {
        Assert.Throws<CommandException>(() => ViewState.Default.Apply(new JsonObject { ["textureRadius"] = 1.5 }));
        Assert.Throws<CommandException>(() => ViewState.Default.Apply(new JsonObject { ["textureBudgetMb"] = "big" }));
    }

    [Fact]
    public void AViewFromAnOlderClient_WithoutTheTextureFields_StillParses_WithTheDefaults()
    {
        JsonObject old = ViewState.Default.ToJson();
        old.Remove("textureRadius");
        old.Remove("textureBudgetMb");

        ViewState view = ViewState.FromJson(old);

        Assert.Equal(1, view.TextureRadius);
        Assert.Equal(384, view.TextureBudgetMb);
    }

    [Fact]
    public async Task ViewSet_PatchesTheTextureFields_AndViewGetReportsThem()
    {
        await using HostRig rig = HostRig.Create();

        await rig.DataAsync("view.set", new JsonObject { ["textureRadius"] = 2, ["textureBudgetMb"] = 512 });
        JsonNode view = await rig.DataAsync("view.get");

        Assert.Equal(2, view.Num("textureRadius"));
        Assert.Equal(512, view.Num("textureBudgetMb"));
    }
}
