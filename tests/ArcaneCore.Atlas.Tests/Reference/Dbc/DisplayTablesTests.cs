using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Host;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Dbc;

/// <summary>
/// The display DBC readers and the resolver chain (design section 2.6): display id -> model data -> model path and skin names, with
/// <c>.mdx</c> names mapped to <c>.m2</c>. Field counts are the ones measured on the 5875 client's own DBC files
/// (CreatureDisplayInfo 12, CreatureModelData 16, CreatureDisplayInfoExtra 19, ItemDisplayInfo 23, GameObjectDisplayInfo 12,
/// CharSections 10, ChrRaces 29, CharHairGeosets 6, CharacterFacialHairStyles 9, AnimationData 7, Emotes 7) and their record sizes (four
/// bytes per field); the synthetic images below use them.
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
        Assert.Equal(6, shapes["CharHairGeosets"]);
        Assert.Equal(9, shapes["CharacterFacialHairStyles"]);
        Assert.False(shapes.ContainsKey("EmotesText"));
    }

    [Fact]
    public void TheExpectedShapes_AlsoPinTheRecordSizeOfEveryTable()
    {
        IReadOnlyDictionary<string, int> sizes = DisplayTables.ExpectedRecordSizes;

        Assert.Equal(DisplayTables.ExpectedFieldCounts.Keys.Order(), sizes.Keys.Order());
        foreach ((string name, int fields) in DisplayTables.ExpectedFieldCounts)
        {
            Assert.Equal(fields * 4, sizes[name]);
        }

        Assert.Equal(92, sizes["ItemDisplayInfo"]);
        Assert.Equal(36, sizes["CharacterFacialHairStyles"]);
    }

    [Fact]
    public void Load_ReportsFieldCountAndRecordSize_OfEachTableItHolds()
    {
        DisplayTables tables = DisplayTables.Load(WolfFiles());

        Assert.Equal(12, tables.FieldCount("CreatureDisplayInfo"));
        Assert.Equal(48, tables.RecordSize("CreatureDisplayInfo"));
        Assert.Equal(64, tables.RecordSize("CreatureModelData"));
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
        CommandResult badKind = await rig.InvokeAsync("display.resolve", new JsonObject { ["kind"] = "npc", ["id"] = 7 });
        CommandResult negative = await rig.InvokeAsync("display.resolve", new JsonObject { ["kind"] = "creature", ["id"] = -1 });

        Assert.Equal(WolfModel, wolf.Str("modelPath"));
        Assert.Empty(wolf["problems"]!.AsArray());
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

    [Fact]
    public void Resolve_Creature_ReportsTheScaleAsTheProductOfDisplayAndModelData()
    {
        MemoryClientFiles files = HumanoidFiles();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution orc = resolver.ResolveCreature(20);

        Assert.Equal(1.5, orc.DisplayScale);
        Assert.Equal(2.0, orc.ModelScale);
        Assert.Equal(3.0, orc.Scale);
        Assert.Null(resolver.ResolveCreature(7).Extra);
        Assert.Equal(1.0, resolver.ResolveCreature(7).Scale);
    }

    [Fact]
    public void Resolve_Creature_FollowsTheExtraRow_ToRaceLookEquipmentAndTheBakedTexture()
    {
        MemoryClientFiles files = HumanoidFiles();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution orc = resolver.ResolveCreature(20);

        DisplayExtra extra = Assert.IsType<DisplayExtra>(orc.Extra);
        Assert.Equal((2, 1, 3, 4, 5, 6, 7), (extra.Race, extra.Sex, extra.Skin, extra.Face, extra.HairStyle, extra.HairColor, extra.FacialHair));
        Assert.Equal([100, 101, 102, 103, 104, 105, 106, 107, 108, 109], extra.Equipment);
        Assert.Equal("Textures\\BakedNpcTextures\\abc123.blp", extra.BakedTexture);
        Assert.Empty(orc.Problems);
    }

    [Fact]
    public void Resolve_Creature_ReportsAMissingExtraRowAndABakedTextureTheArchiveLacks()
    {
        MemoryClientFiles files = HumanoidFiles(bakedPresent: false);
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        Assert.Contains("texture-missing", resolver.ResolveCreature(20).Problems);
        Assert.Equal(["extra-missing"], resolver.ResolveCreature(21).Problems);
        Assert.Null(resolver.ResolveCreature(21).Extra);
    }

    [Fact]
    public void Resolve_Item_ReadsModelsTexturesGeosetsHelmetVisAndTheEightRegions()
    {
        MemoryClientFiles files = HumanoidFiles();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution staff = resolver.ResolveItem(883);
        DisplayResolution robe = resolver.ResolveItem(220);

        ItemDisplay item = Assert.IsType<ItemDisplay>(staff.Item);
        Assert.Equal("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01.m2", staff.ModelPath);
        Assert.Equal("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01.m2", item.Models[0]);
        Assert.Null(item.Models[1]);
        Assert.Equal("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01Teal.blp", item.ModelTextures[0]);
        Assert.Equal("INV_Staff_06", item.Icon);
        Assert.Equal([0, 0, 0], item.GeosetGroups);
        Assert.Empty(staff.Problems);

        ItemDisplay armour = Assert.IsType<ItemDisplay>(robe.Item);
        Assert.Null(robe.ModelPath);
        Assert.Equal([5, 2, 1], armour.GeosetGroups);
        Assert.Equal([245, 368], armour.HelmetGeosetVis);
        Assert.Equal(8, armour.RegionTextures.Count);
        Assert.Equal("Sleeve_AU", armour.RegionTextures[0]);
        Assert.Null(armour.RegionTextures[2]);
        Assert.Equal("Boot_FO", armour.RegionTextures[7]);
        Assert.Empty(robe.Problems);
    }

    [Fact]
    public void Resolve_Item_ReportsAModelNoFolderHolds()
    {
        MemoryClientFiles files = HumanoidFiles();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        Assert.Equal(["model-missing"], resolver.ResolveItem(900).Problems);
        Assert.Equal(["no-row"], resolver.ResolveItem(99999).Problems);
    }

    [Fact]
    public void Resolve_Race_BuildsTheCharacterModelPath_ForEachGender()
    {
        MemoryClientFiles files = HumanoidFiles();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution male = resolver.ResolveRace(2, 0);
        DisplayResolution female = resolver.ResolveRace(2, 1);

        Assert.Equal("Character\\Orc\\Male\\OrcMale.m2", male.ModelPath);
        Assert.Equal("Character\\Orc\\Female\\OrcFemale.m2", female.ModelPath);
        Assert.Equal(new RaceDisplay(2, 1, "Orc", 51, 52), female.Race);
        Assert.Equal(["model-missing"], female.Problems);
        Assert.Empty(male.Problems);
        Assert.Equal(["no-row"], resolver.ResolveRace(77, 0).Problems);
        Assert.Equal(["no-row"], resolver.ResolveRace(2, 2).Problems);
    }

    [Fact]
    public async Task DisplayResolve_Command_ServesItemRaceGenderScaleAndExtra()
    {
        MemoryClientFiles files = HumanoidFiles();
        await using HostRig rig = HostRig.Create(register: context =>
        {
            context.Services.Add<IClientFileSource>(files);
            new DisplayModule().Register(context);
        });

        JsonNode creature = await rig.DataAsync("display.resolve", new JsonObject { ["kind"] = "creature", ["id"] = 20 });
        JsonNode item = await rig.DataAsync("display.resolve", new JsonObject { ["kind"] = "item", ["id"] = 883 });
        JsonNode defaultRace = await rig.DataAsync("display.resolve", new JsonObject { ["kind"] = "race", ["id"] = 2 });
        JsonNode female = await rig.DataAsync("display.resolve", new JsonObject { ["kind"] = "race", ["id"] = 2, ["gender"] = 1 });
        CommandResult badGender = await rig.InvokeAsync("display.resolve", new JsonObject { ["kind"] = "race", ["id"] = 2, ["gender"] = 2 });

        Assert.Equal(3.0, creature.Num("scale"));
        Assert.Equal(1.5, creature.Num("displayScale"));
        Assert.Equal(2.0, creature.Num("modelScale"));
        Assert.Equal(2, creature["extra"]!.Num("race"));
        Assert.Equal(10, creature["extra"]!["equipment"]!.AsArray().Count);
        Assert.Equal("Textures\\BakedNpcTextures\\abc123.blp", creature["extra"]!.Str("bakedTexture"));
        Assert.Equal("INV_Staff_06", item["item"]!.Str("icon"));
        Assert.Equal(8, item["item"]!["regionTextures"]!.AsArray().Count);
        Assert.Equal("Character\\Orc\\Male\\OrcMale.m2", defaultRace.Str("modelPath"));
        Assert.Equal("Character\\Orc\\Female\\OrcFemale.m2", female.Str("modelPath"));
        Assert.Equal("Orc", female["race"]!.Str("clientFile"));
        Assert.Equal("bad-args", badGender.ErrorCode);
    }

    /// <summary>
    /// Rows measured on the real client, written with this layout: CreatureDisplayInfo [3] extra id and [4] scale, CreatureModelData [4]
    /// scale, CreatureDisplayInfoExtra [1..7] look and [8..17] equipment and [18] bake name, ItemDisplayInfo [1] model, [3] texture,
    /// [5] icon, [6..8] geosets (integers, the real maxima are 5, 2 and 1), [9] flags, [12..13] helmet visibility (real range 245..368), [14..21] regions, ChrRaces [4]/[5] display ids and [15] client file.
    /// </summary>
    private static MemoryClientFiles HumanoidFiles(bool bakedPresent = true)
    {
        var files = new MemoryClientFiles();
        static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

        var display = new WdbcTestBuilder(12);
        display.Row((0, 7), (1, 3), (4, Bits(0)), (6, display.String("WolfSkinTimber")), (7, display.String("WolfSkinTimberAlpha")));
        display.Row((0, 20), (1, 4), (3, 5), (4, Bits(1.5f)));
        display.Row((0, 21), (1, 4), (3, 6));
        var modelData = new WdbcTestBuilder(16);
        modelData.Row((0, 3), (2, modelData.String("Creature\\Wolf\\Wolf.mdx")));
        modelData.Row((0, 4), (2, modelData.String("Creature\\OrcMale\\OrcMale.mdx")), (4, Bits(2f)));
        var extra = new WdbcTestBuilder(19);
        extra.Row(
            (0, 5), (1, 2), (2, 1), (3, 3), (4, 4), (5, 5), (6, 6), (7, 7),
            (8, 100), (9, 101), (10, 102), (11, 103), (12, 104), (13, 105), (14, 106), (15, 107), (16, 108), (17, 109),
            (18, extra.String("abc123.blp")));

        var items = new WdbcTestBuilder(23);
        items.Row((0, 883), (1, items.String("Stave_2H_Long_A_01.mdx")), (3, items.String("Stave_2H_Long_A_01Teal")), (5, items.String("INV_Staff_06")));
        items.Row(
            (0, 220), (5, items.String("INV_Robe_02")), (6, 5), (7, 2), (8, 1), (12, 245), (13, 368),
            (14, items.String("Sleeve_AU")), (15, items.String("Sleeve_AL")), (17, items.String("Chest_TU")), (18, items.String("Chest_TL")),
            (19, items.String("Pants_LU")), (20, items.String("Pant_LL")), (21, items.String("Boot_FO")));
        items.Row((0, 900), (1, items.String("Nowhere_01.mdx")));

        var races = new WdbcTestBuilder(29);
        races.Row((0, 2), (4, 51), (5, 52), (15, races.String("Orc")));

        files.Add("DBFilesClient\\CreatureDisplayInfo.dbc", display.Build());
        files.Add("DBFilesClient\\CreatureModelData.dbc", modelData.Build());
        files.Add("DBFilesClient\\CreatureDisplayInfoExtra.dbc", extra.Build());
        files.Add("DBFilesClient\\ItemDisplayInfo.dbc", items.Build());
        files.Add("DBFilesClient\\ChrRaces.dbc", races.Build());
        files.Add("Creature\\OrcMale\\OrcMale.m2", [1]);
        if (bakedPresent)
        {
            files.Add("Textures\\BakedNpcTextures\\abc123.blp", [1]);
        }

        files.Add("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01.m2", [1]);
        files.Add("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01Teal.blp", [1]);
        files.Add("Character\\Orc\\Male\\OrcMale.m2", [1]);
        return files;
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
