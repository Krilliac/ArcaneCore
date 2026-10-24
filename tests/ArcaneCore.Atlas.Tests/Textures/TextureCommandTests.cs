using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Tests.Host;
using ArcaneCore.Atlas.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// <c>texture.get</c>, <c>texture.stats</c> and <c>GET /api/bin/texture</c> over <see cref="TexturesModule"/> (design sections 2.2, 2.4 and
/// the A1 command list). The command twin returns metadata only (binary bytes never go into JSON or MCP results) and writes a PNG
/// through <c>OutputPaths</c> when <c>out</c> is given.
/// </summary>
public sealed class TextureCommandTests
{
    private const string Path4 = "Textures\\Test\\Four.blp";
    private static readonly BlpTestWriter.Px[] Palette4 = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(200, 100, 50)];
    private static readonly byte[] DxtBlock = [0x00, 0x00, 0x00, 0x40, 0xE4, 0x1B, 0x00, 0xFF];

    private static MemoryClientFiles Files()
    {
        var files = new MemoryClientFiles();
        files.Add(Path4, BlpTestWriter.Palettized(2, 2, Palette4, [0, 1, 2, 3], alphaDepth: 8, alphaPlane: [255, 255, 255, 255]));
        files.Add("Textures\\Test\\Dxt.blp", BlpTestWriter.Dxt(4, 4, alphaType: 0, alphaDepth: 1, [DxtBlock]));
        files.Add("Textures\\Test\\Bad.blp", BlpTestWriter.Palettized(2, 2, Palette4, [0, 1, 2, 3], 0, null)[..50]);
        return files;
    }

    private static Action<AtlasContext> WithTextures(MemoryClientFiles? files = null)
        => context =>
        {
            if (files is not null)
            {
                context.Services.Add<IClientFileSource>(files);
            }

            new TexturesModule().Register(context);
        };

    [Fact]
    public void TheModule_RegistersBetweenTheClientAndTheLayers_SoLaterModulesCanAddGenerators()
    {
        var module = new TexturesModule();

        Assert.Equal("textures", module.Name);
        Assert.True(module.Order > ModuleOrder.Foundation && module.Order < ModuleOrder.Layers, $"order {module.Order}");
    }

    [Fact]
    public async Task TheModule_PublishesItsServices_AndItsEndpointAndItsCache()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        Assert.NotNull(rig.Context.Services.Get<TextureCache>());
        Assert.NotNull(rig.Context.Services.Get<GeneratedTextureRegistry>());
        Assert.True(rig.Context.Services.Require<BinaryEndpoints>().TryGet("texture", out _));
        Assert.Contains(rig.Context.Services.Require<CacheParticipants>().All, c => c.Name == "textures");
    }

    [Fact]
    public async Task TextureGet_ByPath_ReturnsMetadataOnly()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        JsonNode data = await rig.DataAsync("texture.get", new JsonObject { ["path"] = "textures/test/FOUR.blp" });

        Assert.Equal("client:textures\\test\\four.blp", data.Str("key"));
        Assert.Equal(Path4, data.Str("path"));
        Assert.Equal("memory", data.Str("archive"));
        Assert.Equal("rgba8", data.Str("format"));
        Assert.Equal(2, data.Num("width"));
        Assert.Equal(2, data.Num("height"));
        Assert.Equal(1, data.Num("layers"));
        Assert.Equal(2, data.Num("levels"));
        Assert.True(data.Flag("hasAlpha"));
        Assert.False(data.Flag("alpha1Bit"));
        Assert.Equal(1, data.Num("source.compression"));
        Assert.Equal(8, data.Num("source.alphaDepth"));
        Assert.Equal(4, data.At("mean")!.AsArray().Count);
        Assert.True(data.Num("wireBytes") > 20);
        Assert.True(data.ToJsonString().Length < 2000, "a texture command must not return pixel data");
    }

    [Fact]
    public async Task TextureGet_ByKey_MatchesByPath_AndAcceptsAMissingKey()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        JsonNode byKey = await rig.DataAsync("texture.get", new JsonObject { ["key"] = "client:Textures\\Test\\Four.blp" });
        JsonNode byPath = await rig.DataAsync("texture.get", new JsonObject { ["path"] = Path4 });
        JsonNode placeholder = await rig.DataAsync("texture.get", new JsonObject { ["key"] = "missing" });

        Assert.Equal(byPath.ToJsonString(), byKey.ToJsonString());
        Assert.Equal(8, placeholder.Num("width"));
    }

    [Fact]
    public async Task TextureGet_DxtDelivery_ReportsTheBlockFormat_AndMaxSizeStepsTheSizeDown()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        JsonNode dxt = await rig.DataAsync("texture.get", new JsonObject { ["path"] = "Textures\\Test\\Dxt.blp", ["fmt"] = "dxt" });
        JsonNode small = await rig.DataAsync("texture.get", new JsonObject { ["path"] = "Textures\\Test\\Dxt.blp", ["maxSize"] = 2 });

        Assert.Equal("dxt1", dxt.Str("format"));
        Assert.True(dxt.Flag("alpha1Bit"));
        Assert.Equal(1, dxt.Num("levels"));
        Assert.Equal(2, small.Num("width"));
        Assert.Equal("rgba8", small.Str("format"));
    }

    [Fact]
    public async Task TextureGet_Out_WritesAPngOfTheChosenMip_OutsideAnyCheckout()
    {
        using var directory = new Reference.Mpq.TestDirectory();
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));
        string at0 = System.IO.Path.Combine(directory.Path, "level0.png");
        string at1 = System.IO.Path.Combine(directory.Path, "level1.png");

        JsonNode zero = await rig.DataAsync("texture.get", new JsonObject { ["path"] = Path4, ["out"] = at0 });
        JsonNode one = await rig.DataAsync("texture.get", new JsonObject { ["path"] = Path4, ["out"] = at1, ["mip"] = 1 });

        byte[] png0 = await File.ReadAllBytesAsync(at0);
        byte[] png1 = await File.ReadAllBytesAsync(at1);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png0[..8]);
        Assert.Equal((2u, 2u), (BinaryPrimitives.ReadUInt32BigEndian(png0.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(png0.AsSpan(20))));
        Assert.Equal((1u, 1u), (BinaryPrimitives.ReadUInt32BigEndian(png1.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(png1.AsSpan(20))));
        Assert.Equal(at0, zero.Str("written.path"));
        Assert.Equal(png0.Length, zero.Num("written.bytes"));
        Assert.Equal(at1, one.Str("written.path"));
    }

    [Fact]
    public async Task TextureGet_Out_IsRefusedInsideTheRepositoryCheckout()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));
        string inside = System.IO.Path.Combine(SourceDirectory(), "should-never-exist.png");

        CommandResult result = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = Path4, ["out"] = inside });

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public async Task TextureGet_Out_NeedsAPngExtension_AndAValidMip()
    {
        using var directory = new Reference.Mpq.TestDirectory();
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        CommandResult wrongExtension = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = Path4, ["out"] = System.IO.Path.Combine(directory.Path, "x.bin") });
        CommandResult badMip = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = Path4, ["out"] = System.IO.Path.Combine(directory.Path, "x.png"), ["mip"] = 9 });
        CommandResult negativeMip = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = Path4, ["mip"] = -1 });

        Assert.Equal(CommandException.BadArguments, wrongExtension.ErrorCode);
        Assert.Equal(CommandException.BadArguments, badMip.ErrorCode);
        Assert.Equal(CommandException.BadArguments, negativeMip.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"key\":\"client:a.blp\",\"path\":\"a.blp\"}")]
    [InlineData("{\"path\":\"Textures\\\\Test\\\\Four.blp\",\"fmt\":\"png\"}")]
    [InlineData("{\"path\":\"Textures\\\\Test\\\\Four.blp\",\"maxSize\":3}")]
    [InlineData("{\"path\":\"Textures\\\\Test\\\\Four.blp\",\"maxSize\":-4}")]
    [InlineData("{\"key\":\"nonsense\"}")]
    public async Task TextureGet_RefusesBadArguments(string json)
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        CommandResult result = await rig.InvokeAsync("texture.get", JsonNode.Parse(json)!.AsObject());

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }

    [Fact]
    public async Task TextureGet_FailuresCarryTheirCause()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));

        CommandResult absent = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = "Textures\\Nothing.blp" });
        CommandResult corrupt = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = "Textures\\Test\\Bad.blp" });
        CommandResult scheme = await rig.InvokeAsync("texture.get", new JsonObject { ["key"] = "gen:nosuch/1" });

        Assert.False(absent.Ok);
        Assert.Contains("not-found", absent.ErrorMessage! + absent.ErrorCode, StringComparison.Ordinal);
        Assert.False(corrupt.Ok);
        Assert.Contains("rejected", corrupt.ErrorMessage! + corrupt.ErrorCode, StringComparison.Ordinal);
        Assert.False(scheme.Ok);
        Assert.Contains("unknown-texture-scheme", scheme.ErrorMessage! + scheme.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAClient_TheCommandsExistAndReportUnavailable()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures());

        CommandResult get = await rig.InvokeAsync("texture.get", new JsonObject { ["path"] = Path4 });
        CommandResult stats = await rig.InvokeAsync("texture.stats");

        Assert.Equal(CommandException.Unavailable, get.ErrorCode);
        Assert.True(stats.Ok, stats.ErrorMessage);
        Assert.Equal(0, stats.Data.Num("count"));
    }

    [Fact]
    public async Task TextureStats_CountsHitsMissesAndListsTheSchemes()
    {
        await using HostRig rig = HostRig.Create(register: context =>
        {
            context.Services.Add<IClientFileSource>(Files());
            new TexturesModule().Register(context);
            context.Services.Require<GeneratedTextureRegistry>().Add(new FlatGenerator("flat"));
        });

        await rig.DataAsync("texture.get", new JsonObject { ["path"] = Path4 });
        await rig.DataAsync("texture.get", new JsonObject { ["path"] = Path4 });
        await rig.DataAsync("texture.get", new JsonObject { ["key"] = "gen:flat/x" });
        JsonNode stats = await rig.DataAsync("texture.stats");

        Assert.Equal(1, stats.Num("hits"));
        Assert.Equal(2, stats.Num("misses"));
        Assert.Equal(1, stats.Num("decodes"));
        Assert.Equal(2, stats.Num("count"));
        Assert.True(stats.Num("bytes") > 0);
        Assert.Equal(["flat"], stats.At("schemes")!.AsArray().Select(s => s!.GetValue<string>()).ToArray());
        Assert.Equal(256L * 1024 * 1024, (long)stats.Num("decodedBudgetBytes"));
        Assert.Equal(128L * 1024 * 1024, (long)stats.Num("wireBudgetBytes"));
    }

    [Fact]
    public async Task Endpoint_WithoutTheToken_Is401()
    {
        await using HostRig rig = await HostRig.StartAsync(register: WithTextures(Files()));

        using HttpResponseMessage response = await rig.Anonymous.GetAsync("api/bin/texture?key=missing");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_ServesAtx1_ForAClientKey_AndTheMissingPlaceholder()
    {
        await using HostRig rig = await HostRig.StartAsync(register: WithTextures(Files()));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/texture?key=client%3Atextures%5Ctest%5Cfour.blp");
        using HttpResponseMessage missing = await rig.Http.GetAsync("api/bin/texture?key=missing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ATX1", Assert.Single(response.Headers.GetValues("X-Atlas-Format")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        AtxTexture texture = TextureWire.Read(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((2, 2, AtxFormat.Rgba8), (texture.Width, texture.Height, texture.Format));
        AtxTexture placeholder = TextureWire.Read(await missing.Content.ReadAsByteArrayAsync());
        Assert.Equal(8, placeholder.Width);
    }

    [Fact]
    public async Task Endpoint_FmtDxt_ReturnsTheFilesOwnBlocks()
    {
        await using HostRig rig = await HostRig.StartAsync(register: WithTextures(Files()));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/texture?key=client%3Atextures%5Ctest%5Cdxt.blp&fmt=dxt");

        AtxTexture texture = TextureWire.Read(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(AtxFormat.Dxt1, texture.Format);
        Assert.Equal(DxtBlock, texture.Levels[0]);
        Assert.True(texture.Flags.HasFlag(AtxFlags.Alpha1Bit));
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?key=nonsense", HttpStatusCode.BadRequest, "bad-key")]
    [InlineData("?key=missing&fmt=png", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?key=missing&maxSize=3", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?key=missing&maxSize=abc", HttpStatusCode.BadRequest, "bad-args")]
    [InlineData("?key=client%3Atextures%5Cnothing.blp", HttpStatusCode.NotFound, "not-found")]
    [InlineData("?key=client%3A..%5C..%5Cwindows%5Cwin.ini", HttpStatusCode.NotFound, "not-found")]
    [InlineData("?key=gen%3Anosuch%2F1", HttpStatusCode.NotFound, "unknown-texture-scheme")]
    [InlineData("?key=client%3Atextures%5Ctest%5Cbad.blp", HttpStatusCode.UnprocessableEntity, "rejected")]
    public async Task Endpoint_MapsEveryFailureToItsStatusAndCode(string query, HttpStatusCode status, string code)
    {
        await using HostRig rig = await HostRig.StartAsync(register: WithTextures(Files()));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/texture" + query);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.Str("error.code"));
    }

    [Fact]
    public async Task Endpoint_ServesAGeneratedKey_FromARegisteredScheme()
    {
        await using HostRig rig = await HostRig.StartAsync(register: context =>
        {
            WithTextures(Files())(context);
            context.Services.Require<GeneratedTextureRegistry>().Add(new FlatGenerator("flat"));
        });

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/texture?key=gen%3Aflat%2Fa%2Fb");

        AtxTexture texture = TextureWire.Read(await response.Content.ReadAsByteArrayAsync());
        Assert.True(texture.Flags.HasFlag(AtxFlags.Generated));
        Assert.Equal("gen:flat/a/b", texture.Meta["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task CacheClear_EmptiesTheTextureCache()
    {
        await using HostRig rig = HostRig.Create(register: WithTextures(Files()));
        await rig.DataAsync("texture.get", new JsonObject { ["path"] = Path4 });
        Assert.Equal(1, (await rig.DataAsync("texture.stats")).Num("count"));

        await rig.DataAsync("cache.clear");

        Assert.Equal(0, (await rig.DataAsync("texture.stats")).Num("count"));
        JsonNode status = await rig.DataAsync("status");
        Assert.Contains(status["caches"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "textures" && c["count"]!.GetValue<int>() == 0);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => System.IO.Path.GetDirectoryName(path)!;

    private sealed class FlatGenerator(string scheme) : IGeneratedTextureSource
    {
        public string Scheme => scheme;

        public AtxTexture Generate(string arguments, CancellationToken cancellationToken)
            => new(AtxFormat.Rgba8, 1, 1, 1, AtxFlags.Generated, [[10, 20, 30, 255]], new JsonObject());
    }
}
