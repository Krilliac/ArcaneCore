using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>
/// The command surface of the model package module that <see cref="ModelPackageTests"/> leaves out: <c>model.info</c> end to end,
/// the <c>model.package out</c> write path, the key form, argument refusals and the no-client replies.
/// </summary>
public sealed class ModelPackageCommandTests
{
    private const string Path = "Creature\\Test\\Thing.m2";
    private const string Skin = "Creature\\Test\\Skin.blp";

    private static Action<AtlasContext> Register(MemoryClientFiles? files)
        => context =>
        {
            if (files is not null)
            {
                context.Services.Add<IClientFileSource>(files);
            }

            new ModelPackagesModule().Register(context);
        };

    private static MemoryClientFiles Files(bool withSkin)
    {
        var files = new MemoryClientFiles();
        files.Add(Path, M2PackageFixtures.TwoBatches());
        if (withSkin)
        {
            files.Add(Skin, [1]);
        }

        return files;
    }

    [Fact]
    public async Task ModelInfo_ByPath_DescribesViewsTexturesBoundsAndSubmeshes()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));

        JsonNode data = await rig.DataAsync("model.info", new JsonObject { ["path"] = "creature/test/THING.m2" });

        Assert.Equal("creature\\test\\THING.m2", data.Str("path"));
        Assert.Equal(4, data.Num("vertices"));
        Assert.Equal(new[] { 0f, 0f, 0f }, data["bounds"]!["min"]!.AsArray().Select(n => n!.GetValue<float>()).ToArray());
        Assert.Equal(new[] { 1f, 1f, 0f }, data["bounds"]!["max"]!.AsArray().Select(n => n!.GetValue<float>()).ToArray());
        JsonNode view = Assert.Single(data["views"]!.AsArray())!;
        Assert.Equal(2, view.Num("triangles"));
        Assert.Equal(2, view.Num("submeshes"));
        Assert.Equal(2, view.Num("textureUnits"));
        Assert.Equal(new[] { 0, 1 }, data["submeshIds"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
        JsonArray textures = data["textures"]!.AsArray();
        Assert.Equal(2, textures.Count);
        Assert.Equal(0, textures[0].Num("type"));
        Assert.Equal(Skin, textures[0].Str("path"));
        Assert.True(textures[0].Flag("exists"));
        Assert.Equal(11, textures[1].Num("type"));
        Assert.Null(textures[1]!["path"]);
    }

    [Fact]
    public async Task ModelInfo_ReportsAMissingTextureFile_AsNotExisting()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: false)));

        JsonNode data = await rig.DataAsync("model.info", new JsonObject { ["path"] = Path });

        Assert.False(data["textures"]![0]!.Flag("exists"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":\"Creature\\\\Test\\\\Thing.m2\",\"kind\":\"creature\",\"id\":1}")]
    [InlineData("{\"kind\":\"creature\"}")]
    [InlineData("{\"id\":4}")]
    [InlineData("{\"kind\":\"item\",\"id\":4}")]
    [InlineData("{\"path\":\"World\\\\Wmo\\\\x.wmo\"}")]
    public async Task ModelInfo_RefusesBadArguments(string json)
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));

        CommandResult result = await rig.InvokeAsync("model.info", JsonNode.Parse(json)!.AsObject());

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }

    [Theory]
    [InlineData("Creature\\Test\\Nothing.m2")]
    [InlineData("..\\..\\windows\\win.ini.m2")]
    [InlineData("C:\\windows\\thing.m2")]
    public async Task ModelInfo_AnAbsentOrEscapingPath_Fails_WithNotFound(string path)
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));

        CommandResult result = await rig.InvokeAsync("model.info", new JsonObject { ["path"] = path });

        Assert.False(result.Ok);
        Assert.Contains("not-found", result.ErrorMessage! + result.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelInfo_ByDisplayId_WithoutTheDisplayModule_IsUnavailable()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));

        CommandResult result = await rig.InvokeAsync("model.info", new JsonObject { ["kind"] = "creature", ["id"] = 7 });

        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
    }

    [Fact]
    public async Task ModelPackage_Out_WritesTheExactAtg1V2Bytes_OutsideAnyCheckout()
    {
        using var directory = new Mpq.TestDirectory();
        await using HostRig rig = await HostRig.StartAsync(register: Register(Files(withSkin: true)));
        string target = System.IO.Path.Combine(directory.Path, "thing.atg");

        JsonNode data = await rig.DataAsync("model.package", new JsonObject { ["path"] = Path, ["out"] = target });
        using HttpResponseMessage served = await rig.Http.GetAsync("api/bin/model?path=Creature%5CTest%5CThing.m2");

        byte[] written = await File.ReadAllBytesAsync(target);
        Assert.Equal(await served.Content.ReadAsByteArrayAsync(), written);
        Assert.Equal(target, data.Str("written.path"));
        Assert.Equal(written.Length, data.Num("written.bytes"));
        Assert.Equal(written.Length, data.Num("wireBytes"));
        Assert.True(GeometryWire.Read(written).IsV2);
    }

    [Fact]
    public async Task ModelPackage_Out_IsRefusedInsideTheCheckout_AndNeedsTheAtgExtension()
    {
        using var directory = new Mpq.TestDirectory();
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));
        string inside = System.IO.Path.Combine(SourceDirectory(), "should-never-exist.atg");

        CommandResult checkout = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = Path, ["out"] = inside });
        CommandResult extension = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = Path, ["out"] = System.IO.Path.Combine(directory.Path, "x.png") });

        Assert.Equal(CommandException.BadArguments, checkout.ErrorCode);
        Assert.False(File.Exists(inside));
        Assert.Equal(CommandException.BadArguments, extension.ErrorCode);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ModelPackage_AFailedBuild_WritesNothing()
    {
        using var directory = new Mpq.TestDirectory();
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));
        string target = System.IO.Path.Combine(directory.Path, "missing.atg");

        CommandResult result = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = "Creature\\Test\\Nothing.m2", ["out"] = target });

        Assert.False(result.Ok);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ModelPackage_TheKeyForm_MatchesThePathForm_AndBothAtOnceIsRefused()
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));

        JsonNode byKey = await rig.DataAsync("model.package", new JsonObject { ["key"] = "m2:creature\\test\\thing.m2" });
        JsonNode byPath = await rig.DataAsync("model.package", new JsonObject { ["path"] = Path });
        CommandResult both = await rig.InvokeAsync("model.package", new JsonObject { ["key"] = "m2:creature\\test\\thing.m2", ["path"] = Path });

        Assert.Equal(byPath.Str("key"), byKey.Str("key"));
        Assert.Equal(byPath.Num("wireBytes"), byKey.Num("wireBytes"));
        Assert.Equal(CommandException.BadArguments, both.ErrorCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task ModelPackage_ViewOutsideZeroToThree_IsRefused(int view)
    {
        await using HostRig rig = HostRig.Create(register: Register(Files(withSkin: true)));

        CommandResult result = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = Path, ["view"] = view });

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }

    [Fact]
    public async Task WithoutAClient_BothCommandsExistAndReportUnavailable()
    {
        await using HostRig rig = HostRig.Create(register: Register(null));

        CommandResult info = await rig.InvokeAsync("model.info", new JsonObject { ["path"] = Path });
        CommandResult package = await rig.InvokeAsync("model.package", new JsonObject { ["path"] = Path });

        Assert.Equal(CommandException.Unavailable, info.ErrorCode);
        Assert.Equal(CommandException.Unavailable, package.ErrorCode);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => System.IO.Path.GetDirectoryName(path)!;
}
