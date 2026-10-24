using System.Net;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>
/// The minimal M2 package (design section 2.5): one view of a model as an ATG1 v2 chunk (positions, normals, uv0, one batch per
/// texture unit, a "generic" material per batch) plus <c>model.info</c>'s texture list. Skinning, animation and the render-flag
/// to shader mapping beyond opaque / alpha key / unlit / two-sided belong to lane A3 and are not pinned here.
/// </summary>
public sealed class ModelPackageTests
{
    private const string Path = "Creature\\Test\\Thing.m2";

    private static Action<AtlasContext> Register(MemoryClientFiles files)
        => context =>
        {
            context.Services.Add<IClientFileSource>(files);
            new ModelPackagesModule().Register(context);
        };

    private static MemoryClientFiles Files(byte[]? m2 = null)
    {
        var files = new MemoryClientFiles();
        files.Add(Path, m2 ?? M2PackageFixtures.TwoBatches());
        return files;
    }

    [Fact]
    public async Task ModelPackage_ReturnsOneBatchPerTextureUnit_WithTheirRanges()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files()));

        JsonNode data = await rig.DataAsync("model.package", new JsonObject { ["path"] = "creature/test/THING.m2" });

        Assert.Equal(Path, data.Str("path"));
        Assert.Equal(4, data.Num("vertices"));
        Assert.Equal(2, data.Num("triangles"));
        JsonArray batches = data["batches"]!.AsArray();
        Assert.Equal(2, batches.Count);
        Assert.Equal(0, batches[0].Num("firstTriangle"));
        Assert.Equal(1, batches[0].Num("triangleCount"));
        Assert.Equal(1, batches[1].Num("firstTriangle"));
        Assert.Equal(1, batches[1].Num("triangleCount"));
        Assert.True(data.Num("wireBytes") > GeometryWire.HeaderBytesV2);
    }

    [Fact]
    public async Task ModelPackage_MapsRenderFlagsAndTextures_ToGenericMaterials()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files()));

        JsonNode data = await rig.DataAsync("model.package", new JsonObject { ["path"] = Path });

        Material first = Material.FromJson(data["batches"]![0]!["material"]!.AsObject());
        Material second = Material.FromJson(data["batches"]![1]!["material"]!.AsObject());
        Assert.Equal("generic", first.Kind);
        Assert.Equal(BlendMode.Opaque, first.Blend);
        Assert.Equal(MaterialFlags.None, first.Flags & (MaterialFlags.TwoSided | MaterialFlags.Unlit));
        Assert.Equal("client:creature\\test\\skin.blp", first.Textures[0].Key);
        Assert.Null(first.Textures[0].Replace);
        Assert.Equal(BlendMode.AlphaKey, second.Blend);
        Assert.True(second.Flags.HasFlag(MaterialFlags.TwoSided));
        Assert.Equal(11, second.Textures[0].Replace);
        Assert.Equal("missing", second.Textures[0].Key);
    }

    [Fact]
    public async Task TheBinaryEndpoint_ServesAnAtg1V2Chunk_WithUv0AndAModelBlock()
    {
        await using HostRig rig = await HostRig.StartAsync(register: Register(Files()));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/model?path=Creature%5CTest%5CThing.m2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ATG1", Assert.Single(response.Headers.GetValues("X-Atlas-Format")));
        GeometryChunk chunk = GeometryWire.Read(await response.Content.ReadAsByteArrayAsync());
        Assert.True(chunk.IsV2);
        Assert.Equal(4, chunk.VertexCount);
        Assert.Equal(2, chunk.Batches!.Count);
        Assert.Equal(2, chunk.Materials!.Count);
        Assert.Equal((0.25f, 0.75f), (chunk.Uv0![0].X, chunk.Uv0[0].Y));
        Assert.Equal("m2:creature\\test\\thing.m2", chunk.Model!["key"]!.GetValue<string>());
        Assert.Equal("m2", chunk.Model["kind"]!.GetValue<string>());
        Assert.Equal(LayerIds.RefM2Tex, chunk.LayerId);
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?path=Creature%5CTest%5CNothing.m2", HttpStatusCode.NotFound, "not-found")]
    [InlineData("?path=Creature%5CTest%5CThing.m2&view=5", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?path=Creature%5CTest%5CThing.m2&view=-1", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?path=Creature%5CTest%5CThing.m2&view=x", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?path=..%5C..%5Cwindows%5Cwin.ini", HttpStatusCode.NotFound, "not-found")]
    [InlineData("?path=Creature%5CTest%5CThing.txt", HttpStatusCode.BadRequest, "bad-args")]
    public async Task TheBinaryEndpoint_MapsFailures(string query, HttpStatusCode status, string code)
    {
        await using HostRig rig = await HostRig.StartAsync(register: Register(Files()));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/model" + query);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.Str("error.code"));
    }

    [Fact]
    public async Task TheBinaryEndpoint_RequiresTheToken()
    {
        await using HostRig rig = await HostRig.StartAsync(register: Register(Files()));

        using HttpResponseMessage response = await rig.Anonymous.GetAsync("api/bin/model?path=Creature%5CTest%5CThing.m2");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ModelPackage_RefusesACorruptModel_AsRejected()
    {
        byte[] bad = M2PackageFixtures.TwoBatches()[..0x60];
        await using HostRig rig = await HostRig.StartAsync(register: Register(Files(bad)));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/model?path=Creature%5CTest%5CThing.m2");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ModelPackage_ATextureUnitPointingPastTheLookup_IsAProblem_NotACrash()
    {
        byte[] m2 = M2PackageFixtures.Build(
            [new(0, 0, 0, 0, 0), new(1, 0, 0, 1, 0), new(1, 1, 0, 1, 1)],
            [0, 1, 2],
            [new(0, 0, 3)],
            [new(0, 0, 9)],
            [new(0, "Creature\\Test\\Skin.blp")],
            [(0, 0)],
            [0]);
        await using HostRig rig = HostRig.Create(register: Register(Files(m2)));

        JsonNode data = await rig.DataAsync("model.package", new JsonObject { ["path"] = Path });

        Assert.Contains(data["problems"]!.AsArray(), p => p!.GetValue<string>() == "bad-texture-lookup");
        Assert.Equal(1, data.Num("triangles"));
    }

    [Fact]
    public async Task ModelPackage_AModelWithoutTextureUnits_StillPackagesItsTrianglesAsOneBatch()
    {
        byte[] m2 = M2PackageFixtures.Build(
            [new(0, 0, 0, 0, 0), new(1, 0, 0, 1, 0), new(1, 1, 0, 1, 1)],
            [0, 1, 2],
            [new(0, 0, 3)],
            [],
            [],
            [],
            []);
        await using HostRig rig = HostRig.Create(register: Register(Files(m2)));

        JsonNode data = await rig.DataAsync("model.package", new JsonObject { ["path"] = Path });

        JsonArray batches = data["batches"]!.AsArray();
        Assert.Single(batches);
        Assert.Equal(1, batches[0].Num("triangleCount"));
        Assert.Empty(Material.FromJson(batches[0]!["material"]!.AsObject()).Textures);
    }

    [Fact]
    public async Task ModelPackage_Bad_Arguments()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files()));

        CommandResult none = await rig.InvokeAsync("model.package", new JsonObject());
        CommandResult view = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = Path, ["view"] = 3 });
        CommandResult wmo = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = "World\\Wmo\\x.wmo" });

        Assert.Equal(CommandException.BadArguments, none.ErrorCode);
        Assert.Equal(CommandException.BadArguments, view.ErrorCode);
        Assert.False(wmo.Ok);
    }

    [Fact]
    public async Task TheCache_IsAParticipant_AndClears()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files()));
        await rig.DataAsync("model.package", new JsonObject { ["path"] = Path });

        JsonNode before = await rig.DataAsync("status");
        await rig.DataAsync("cache.clear");
        JsonNode after = await rig.DataAsync("status");

        JsonNode Entry(JsonNode s) => s["caches"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "model-packages")!;
        Assert.Equal(1, Entry(before).Num("count"));
        Assert.Equal(0, Entry(after).Num("count"));
    }
}
