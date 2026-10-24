using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Display;
using ArcaneCore.Atlas.Tests.Host;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Dbc;

/// <summary>
/// The display DBC readers and the resolver chain (design section 2.6): display id -> model data -> model path and skin names, with
/// <c>.mdx</c> names mapped to <c>.m2</c>. Field counts are the ones measured on the 5875 client's own DBC files
/// (CreatureDisplayInfo 12, CreatureModelData 16, CreatureDisplayInfoExtra 19, ItemDisplayInfo 23, GameObjectDisplayInfo 12,
/// CharSections 10, ChrRaces 29, AnimationData 7, Emotes 7, EmotesText 19, CharHairGeosets 6); the synthetic images below use them.
/// </summary>
public sealed class DisplayTablesTests
{
    private const string WolfModel = "Creature\\Wolf\\Wolf.m2";
    private const string WolfSkin = "Creature\\Wolf\\WolfSkinTimber.blp";

    [Fact]
    public void TheExpectedShapes_ListElevenTables_WithTheMeasuredFieldCounts()
    {
        IReadOnlyDictionary<string, int> shapes = DisplayTables.ExpectedFieldCounts;

        Assert.Equal(11, shapes.Count);
        Assert.Equal(12, shapes["CreatureDisplayInfo"]);
        Assert.Equal(16, shapes["CreatureModelData"]);
        Assert.Equal(19, shapes["CreatureDisplayInfoExtra"]);
        Assert.Equal(23, shapes["ItemDisplayInfo"]);
        Assert.Equal(12, shapes["GameObjectDisplayInfo"]);
        Assert.Equal(10, shapes["CharSections"]);
        Assert.Equal(29, shapes["ChrRaces"]);
        Assert.Equal(7, shapes["AnimationData"]);
        Assert.Equal(7, shapes["Emotes"]);
        Assert.Equal(19, shapes["EmotesText"]);
        Assert.Equal(6, shapes["CharHairGeosets"]);
    }

    [Fact]
    public void Load_RefusesATableWithTheWrongFieldCount_NamingIt()
    {
        MemoryClientFiles files = WolfFiles(creatureDisplayFields: 11);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => DisplayTables.Load(files));

        Assert.Contains("CreatureDisplayInfo", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ToleratesAbsentTables_AndReportsThemAsUnavailable()
    {
        var files = new MemoryClientFiles();

        DisplayTables tables = DisplayTables.Load(files);

        Assert.False(tables.Has("CreatureDisplayInfo"));
        Assert.Empty(tables.Available);
    }

    [Fact]
    public void Resolve_Creature_FollowsDisplayToModelData_AndMapsMdxToM2()
    {
        DisplayTables tables = DisplayTables.Load(WolfFiles());
        var resolver = new DisplayResolver(tables, WolfFiles());

        DisplayResolution wolf = resolver.ResolveCreature(7);

        Assert.Equal(WolfModel, wolf.ModelPath);
        Assert.Equal(3, wolf.ModelDataId);
        Assert.Equal(WolfSkin, wolf.Textures[0]);
        Assert.Equal("Creature\\Wolf\\WolfSkinTimberAlpha.blp", wolf.Textures[1]);
        Assert.Null(wolf.Textures[2]);
        Assert.Empty(wolf.Problems);
    }

    [Fact]
    public void Resolve_Creature_ReportsEachBreakInTheChain_NotAnException()
    {
        DisplayTables tables = DisplayTables.Load(WolfFiles());
        var empty = new MemoryClientFiles();
        var resolver = new DisplayResolver(tables, empty);

        DisplayResolution noRow = resolver.ResolveCreature(99999);
        DisplayResolution missing = resolver.ResolveCreature(7);

        Assert.Equal(["no-row"], noRow.Problems);
        Assert.Null(noRow.ModelPath);
        Assert.Contains("model-missing", missing.Problems);
        Assert.Contains("texture-missing", missing.Problems);
        Assert.Equal(WolfModel, missing.ModelPath);
    }

    [Fact]
    public void Resolve_GameObject_UsesTheModelNameDirectly()
    {
        MemoryClientFiles files = WolfFiles();
        files.Add("World\\Generic\\ActiveDoodads\\Chest02\\Chest02.m2", [1]);
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution chest = resolver.ResolveGameObject(1);

        Assert.Equal("World\\Generic\\ActiveDoodads\\Chest02\\Chest02.m2", chest.ModelPath);
        Assert.Empty(chest.Problems);
    }

    [Fact]
    public async Task DisplayResolve_Command_ReturnsTheChain_AndRejectsBadArguments()
    {
        MemoryClientFiles files = WolfFiles();
        await using HostRig rig = HostRig.Create(register: context =>
        {
            context.Services.Add<IClientFileSource>(files);
            new DisplayModule().Register(context);
        });

        JsonNode wolf = await rig.DataAsync("display.resolve", new JsonObject { ["kind"] = "creature", ["id"] = 7 });
        CommandResult noKind = await rig.InvokeAsync("display.resolve", new JsonObject { ["id"] = 7 });
        CommandResult badKind = await rig.InvokeAsync("display.resolve", new JsonObject { ["kind"] = "item", ["id"] = 7 });
        CommandResult negative = await rig.InvokeAsync("display.resolve", new JsonObject { ["kind"] = "creature", ["id"] = -1 });

        Assert.Equal(WolfModel, wolf.Str("modelPath"));
        Assert.Equal(0, wolf["problems"]!.AsArray().Count);
        Assert.Equal(WolfSkin, wolf["textures"]![0]!.GetValue<string>());
        Assert.Equal("bad-args", noKind.ErrorCode);
        Assert.Equal("bad-args", badKind.ErrorCode);
        Assert.Equal("bad-args", negative.ErrorCode);
    }

    [Fact]
    public async Task DisplayResolve_ReportsNoRow_AsAProblem_NotAnError()
    {
        await using HostRig rig = HostRig.Create(register: context =>
        {
            context.Services.Add<IClientFileSource>(WolfFiles());
            new DisplayModule().Register(context);
        });

        JsonNode data = await rig.DataAsync("display.resolve", new JsonObject { ["kind"] = "creature", ["id"] = 424242 });

        Assert.Equal("no-row", data["problems"]![0]!.GetValue<string>());
    }

    /// <summary>A client with the wolf's rows in CreatureDisplayInfo (id 7) and CreatureModelData (id 3), and the wolf's files.</summary>
    private static MemoryClientFiles WolfFiles(int creatureDisplayFields = 12)
    {
        var display = new WdbcTestBuilder(creatureDisplayFields);
        // 5875 layout, measured: [0] id, [1] model data id, [6] and [7] are the first two skin names (string offsets).
        display.Row((0, 7), (1, 3), (6, display.String("WolfSkinTimber")), (7, display.String("WolfSkinTimberAlpha")));

        var modelData = new WdbcTestBuilder(16);
        modelData.Row((0, 3), (2, modelData.String("Creature\\Wolf\\Wolf.mdx")));

        var objects = new WdbcTestBuilder(12);
        objects.Row((0, 1), (1, objects.String("World\\Generic\\ActiveDoodads\\Chest02\\Chest02.mdx")));

        var files = new MemoryClientFiles();
        files.Add("DBFilesClient\\CreatureDisplayInfo.dbc", display.Build());
        files.Add("DBFilesClient\\CreatureModelData.dbc", modelData.Build());
        files.Add("DBFilesClient\\GameObjectDisplayInfo.dbc", objects.Build());
        files.Add(WolfModel, [1]);
        files.Add(WolfSkin, [1]);
        files.Add("Creature\\Wolf\\WolfSkinTimberAlpha.blp", [1]);
        return files;
    }
}
