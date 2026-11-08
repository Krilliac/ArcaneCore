using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Navmesh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

/// <summary>
/// <c>ref.nav.go</c>: the transports' <c>goNNNN.mmtile</c> files (vmangos MMapManager::loadGameObject, MoveMap.cpp:374-433) are listed, validated with
/// the terrain tile reader and described in model coordinates. Written against synthetic tiles with the same file format as a terrain tile.
/// </summary>
public sealed class GoNavCommandTests : IDisposable
{
    private readonly SyntheticMap _data = new();
    private AtlasContext? _context;

    public void Dispose()
    {
        _context?.Dispose();
        _data.Dispose();
    }

    private AtlasContext Context => _context ??= AtlasModules.Compose(new AtlasPaths(_data.Root, null), NullLoggerFactory.Instance, headless: true, [new ReferenceNavmeshModule()]);

    private void WriteGo(uint displayId, int cells = 4)
    {
        BuiltTile built = SyntheticNav.Build(new TileSpec
        {
            TerrainX = 32,
            TerrainY = 32,
            Cells = cells,
            Height = 10f,
            Present = (_, _) => true,
            Flags = (_, _) => 1,
            OffMesh = [],
        });
        File.WriteAllBytes(Path.Combine(_data.Mmaps, GoNavCommands.FileName(displayId)), built.File);
    }

    private async Task<CommandResult> Run(JsonObject args)
        => await Context.Commands.InvokeAsync(GoNavCommands.Name, args, Context.Services, CommandOrigin.Script, headless: true, CancellationToken.None);

    [Theory]
    [InlineData(7u, "go0007.mmtile")]
    [InlineData(1234u, "go1234.mmtile")]
    [InlineData(12345u, "go12345.mmtile")]
    public void TheFileName_IsVmangosGoPercent04i(uint displayId, string expected)
        => Assert.Equal(expected, GoNavCommands.FileName(displayId));

    [Theory]
    [InlineData("go0007.mmtile", true, 7u)]
    [InlineData("GO12345.MMTILE", true, 12345u)]
    [InlineData("go.mmtile", false, 0u)]
    [InlineData("gox.mmtile", false, 0u)]
    [InlineData("0000032.mmtile", false, 0u)]
    [InlineData("go0007.mmap", false, 0u)]
    [InlineData("go-5.mmtile", false, 0u)]
    public void TheFileNameParser_AcceptsOnlyGoDigitsMmtile(string name, bool ok, uint expected)
    {
        Assert.Equal(ok, GoNavCommands.TryParseFileName(name, out uint id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public async Task WithoutADisplayId_TheFilesAreListedAscending_AndTerrainTilesAreNotCounted()
    {
        WriteGo(300);
        WriteGo(7);
        File.WriteAllBytes(Path.Combine(_data.Mmaps, "notatile.txt"), [1]);

        CommandResult result = await Run(new JsonObject());

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = JsonNode.Parse(result.Data!.ToJsonString())!;
        Assert.Equal(2, data["count"]!.GetValue<int>());
        Assert.Equal([7u, 300u], data["displayIds"]!.AsArray().Select(n => n!.GetValue<uint>()).ToArray());
        Assert.False(data["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WithADisplayId_TheTileIsDescribedInModelCoordinates_WithItsPolygonCounts()
    {
        WriteGo(42, cells: 4);

        CommandResult result = await Run(new JsonObject { ["displayId"] = 42 });

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = JsonNode.Parse(result.Data!.ToJsonString())!;
        Assert.Equal("go0042.mmtile", data["file"]!.GetValue<string>());
        Assert.Equal("model", data["coordinates"]!.GetValue<string>());
        int polys = data["header"]!["polyCount"]!.GetValue<int>();
        Assert.True(polys > 0);
        Assert.Equal(polys, data["walkablePolys"]!.GetValue<int>() + data["offMeshPolys"]!.GetValue<int>());
        Assert.Equal(0, data["offMeshPolys"]!.GetValue<int>());
        Assert.Equal(polys, data["colorKeys"]!.AsObject().Sum(kv => kv.Value!.GetValue<int>()));
        Assert.Equal(3, data["header"]!["boundsMin"]!.AsArray().Count);
    }

    [Fact]
    public async Task AMissingTile_IsUnavailable_AndACorruptOne_IsRefusedWithItsReason()
    {
        CommandResult missing = await Run(new JsonObject { ["displayId"] = 9 });
        Assert.False(missing.Ok);
        Assert.Equal(CommandException.Unavailable, missing.ErrorCode);

        File.WriteAllBytes(Path.Combine(_data.Mmaps, GoNavCommands.FileName(9)), new byte[64]);
        CommandResult corrupt = await Run(new JsonObject { ["displayId"] = 9 });
        Assert.False(corrupt.Ok);
        Assert.Equal(CommandException.Failed, corrupt.ErrorCode);
        Assert.Contains("go0009.mmtile was refused", corrupt.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANegativeOrHugeDisplayId_IsBadArguments()
    {
        CommandResult negative = await Run(new JsonObject { ["displayId"] = -1 });
        Assert.Equal(CommandException.BadArguments, negative.ErrorCode);
        CommandResult huge = await Run(new JsonObject { ["displayId"] = 5_000_000_000L });
        Assert.Equal(CommandException.BadArguments, huge.ErrorCode);
    }
}
