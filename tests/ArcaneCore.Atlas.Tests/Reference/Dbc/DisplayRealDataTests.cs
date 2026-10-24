using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Dbc;

/// <summary>
/// Display resolution against the real 5875 client. The expected values were read out of the client's own DBC files and MPQ chain
/// when these tests were written (display 372 is the Wolf: model data 43, "Creature\Wolf\Wolf.mdx", skins WolfSkinTimber and
/// WolfSkinTimberAlpha; gameobject display 1 is "World\Generic\ActiveDoodads\Chest02\Chest02.mdx"). Skipped visibly unless
/// ARCANECORE_TEST_CLIENT_DIR is set; nothing from the client is stored in the repository.
/// </summary>
[Collection("Atlas A1 real client textures")]
public sealed class DisplayRealDataTests
{
    [ClientArchiveFact]
    public void TheElevenTables_ExistWithTheMeasuredFieldCounts()
    {
        using var files = RealTextureData.Open();
        DisplayTables tables = DisplayTables.Load(files);

        foreach ((string name, int fields) in DisplayTables.ExpectedFieldCounts)
        {
            Assert.True(tables.Has(name), name);
            Assert.Equal(fields, tables.FieldCount(name));
        }

        Assert.Equal(10534, tables.RecordCount("CreatureDisplayInfo"));
        Assert.Equal(430, tables.RecordCount("CreatureModelData"));
        Assert.Equal(1638, tables.RecordCount("GameObjectDisplayInfo"));
        Assert.Equal(9, tables.RecordCount("ChrRaces"));
        Assert.Equal(78, tables.RecordCount("Emotes"));
        Assert.Equal(136, tables.RecordCount("CharacterFacialHairStyles"));
        foreach ((string name, int size) in DisplayTables.ExpectedRecordSizes)
        {
            Assert.Equal(size, tables.RecordSize(name));
        }
    }

    [ClientArchiveFact]
    public void CreatureDisplay372_IsTheWolf_WithItsFilesPresentInTheChain()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution wolf = resolver.ResolveCreature(372);

        Assert.Equal("Creature\\Wolf\\Wolf.m2", wolf.ModelPath);
        Assert.Equal(43, wolf.ModelDataId);
        Assert.Equal("Creature\\Wolf\\WolfSkinTimber.blp", wolf.Textures[0]);
        Assert.Equal("Creature\\Wolf\\WolfSkinTimberAlpha.blp", wolf.Textures[1]);
        Assert.Empty(wolf.Problems);
        Assert.True(files.Exists(wolf.ModelPath!));
        Assert.True(wolf.Scale > 0);
        Assert.Null(wolf.Extra);
    }

    [ClientArchiveFact]
    public void CreatureDisplay115_IsAHumanoidWithAnExtraRow_AndItsBakedTextureExists()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution humanoid = resolver.ResolveCreature(115);

        DisplayExtra extra = Assert.IsType<DisplayExtra>(humanoid.Extra);
        Assert.InRange(extra.Race, 1, 8);
        Assert.InRange(extra.Sex, 0, 1);
        // The 1.12.1 row 23 has 19 fields: id, race, sex, five look fields, ten equipment slots at 8..17 (head, shoulder, shirt, chest,
        // belt, legs, boots, wrist, gloves, tabard) and the baked name at 18. Known non-zero item display ids pin the slot columns, and
        // each must be an ItemDisplayInfo row (a 9-items-plus-flags layout would put a flag where a display id belongs).
        Assert.Equal([8815, 8815, 3900, 0, 8328, 5848, 8816, 0, 3052, 0], extra.Equipment);
        foreach (int itemDisplay in extra.Equipment.Where(id => id != 0))
        {
            Assert.DoesNotContain("no-row", resolver.ResolveItem(itemDisplay).Problems);
        }

        Assert.StartsWith("Textures\\BakedNpcTextures\\", extra.BakedTexture, StringComparison.Ordinal);
        Assert.True(files.Exists(extra.BakedTexture!));
        Assert.True(files.Exists(humanoid.ModelPath!));
        // The display names a skin variation (a dwarf warrior skin) that is not a file next to the model: a humanoid wears its baked
        // texture instead, so only that skin lookup may be reported, never a missing model or extra row.
        Assert.DoesNotContain("model-missing", humanoid.Problems);
        Assert.DoesNotContain("extra-missing", humanoid.Problems);
    }

    [ClientArchiveFact]
    public void ItemDisplay883_IsAStaff_WithItsModelAndTextureInTheWeaponFolder()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution staff = resolver.ResolveItem(883);

        ItemDisplay item = Assert.IsType<ItemDisplay>(staff.Item);
        Assert.Equal("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01.m2", staff.ModelPath);
        Assert.Equal("Item\\ObjectComponents\\Weapon\\Stave_2H_Long_A_01Teal.blp", item.ModelTextures[0]);
        Assert.Equal("INV_Staff_06", item.Icon);
        Assert.Empty(staff.Problems);
    }

    [ClientArchiveFact]
    public void ItemDisplay222_IsABoot_AndItemDisplay220_HasArmRegionTextures()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution boots = resolver.ResolveItem(222);
        DisplayResolution robe = resolver.ResolveItem(220);

        Assert.Equal("INV_Boots_01", boots.Item!.Icon);
        Assert.Equal("Generic_HuMa_01_Boot_FO", boots.Item.RegionTextures[7]);
        Assert.Null(boots.ModelPath);
        Assert.Equal("Generic_HuWk_01_Sleeve_AU", robe.Item!.RegionTextures[0]);
        Assert.Equal("Generic_HuWk_01_Sleeve_AL", robe.Item.RegionTextures[1]);
        Assert.Equal("Generic_HuWk_01_Chest_TU", robe.Item.RegionTextures[3]);
        Assert.Equal("Generic_HuWk_01_Pant_LL", robe.Item.RegionTextures[6]);
    }

    [ClientArchiveFact]
    public void EveryRace_HasAClientFileFolder_AndTheHumansAndOrcsHaveTheirModels()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution human = resolver.ResolveRace(1, 0);
        DisplayResolution orcWoman = resolver.ResolveRace(2, 1);

        Assert.Equal("Character\\Human\\Male\\HumanMale.m2", human.ModelPath);
        Assert.Equal(new RaceDisplay(1, 0, "Human", 49, 50), human.Race);
        Assert.Empty(human.Problems);
        Assert.Equal("Character\\Orc\\Female\\OrcFemale.m2", orcWoman.ModelPath);
        Assert.Empty(orcWoman.Problems);
        for (int race = 1; race <= 8; race++)
        {
            Assert.False(string.IsNullOrEmpty(resolver.ResolveRace(race, 0).Race!.ClientFile), $"race {race}");
        }
    }

    [ClientArchiveFact]
    public void GameObjectDisplay1_IsTheChest()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        DisplayResolution chest = resolver.ResolveGameObject(1);

        Assert.Equal("World\\Generic\\ActiveDoodads\\Chest02\\Chest02.m2", chest.ModelPath);
        Assert.Empty(chest.Problems);
    }

    [ClientArchiveFact]
    public void ItemDisplayInfo_GeosetColumns6To8_AreIntegersNotStringOffsets_AndHelmetVisIs12And13()
    {
        using var files = RealTextureData.Open();
        DisplayTables tables = DisplayTables.Load(files);
        WdbcFile items = tables.Table("ItemDisplayInfo");
        var resolver = new DisplayResolver(tables, files);

        // Known rows: the staff has no geosets; display 220 (a robe) sets columns 6 and 8, 365 only column 7, 1867 only column 8.
        Assert.Equal([0, 0, 0], resolver.ResolveItem(883).Item!.GeosetGroups);
        Assert.Equal([1, 0, 1], resolver.ResolveItem(220).Item!.GeosetGroups);
        Assert.Equal([0, 1, 0], resolver.ResolveItem(365).Item!.GeosetGroups);
        Assert.Equal([0, 0, 1], resolver.ResolveItem(1867).Item!.GeosetGroups);
        // Helmet visibility lives at 12 and 13 (HelmetGeosetVisData ids); a helmet row pins it.
        Assert.Equal([249, 305], resolver.ResolveItem(14903).Item!.HelmetGeosetVis);
        Assert.Equal([247, 247], resolver.ResolveItem(14904).Item!.HelmetGeosetVis);

        // Over every row: columns 6..8 never hold a string offset (all are tiny: a ground-model string would be >= 1 and point into the
        // string block, and the icon string offsets in this table are in the thousands), column 12 and 13 are 0 or 245..368, and 14..21
        // are string offsets that resolve.
        uint maxCol6 = 0, maxCol7 = 0, maxCol8 = 0;
        for (int row = 0; row < items.RecordCount; row++)
        {
            maxCol6 = Math.Max(maxCol6, items.GetUInt32(row, 6));
            maxCol7 = Math.Max(maxCol7, items.GetUInt32(row, 7));
            maxCol8 = Math.Max(maxCol8, items.GetUInt32(row, 8));
            foreach (int col in new[] { 12, 13 })
            {
                uint vis = items.GetUInt32(row, col);
                Assert.True(vis == 0 || vis is >= 245 and <= 368, $"row {row} col {col} = {vis}");
            }

            for (int col = 14; col <= 21; col++)
            {
                _ = items.GetString(row, col);
            }
        }

        Assert.Equal(29604, items.RecordCount);
        Assert.Equal((5u, 2u, 1u), (maxCol6, maxCol7, maxCol8));
    }

    [ClientArchiveFact]
    public void ChrRaces_Columns4_5_And15_PinEveryRace()
    {
        using var files = RealTextureData.Open();
        DisplayTables tables = DisplayTables.Load(files);
        WdbcFile races = tables.Table("ChrRaces");

        (int Id, uint Male, uint Female, string File)[] expected =
        [
            (1, 49, 50, "Human"), (2, 51, 52, "Orc"), (3, 53, 54, "Dwarf"), (4, 55, 56, "NightElf"), (5, 57, 58, "Scourge"),
            (6, 59, 60, "Tauren"), (7, 1563, 1564, "Gnome"), (8, 1478, 1479, "Troll"), (9, 1140, 1140, "Goblin"),
        ];
        Assert.Equal(expected.Length, races.RecordCount);
        foreach ((int id, uint male, uint female, string file) in expected)
        {
            int row = tables.FindRow("ChrRaces", (uint)id);
            Assert.True(row >= 0, $"race {id}");
            Assert.Equal(male, races.GetUInt32(row, 4));
            Assert.Equal(female, races.GetUInt32(row, 5));
            Assert.Equal(file, races.GetString(row, 15));
        }
    }

    [ClientArchiveFact]
    public void CreatureDisplayInfoExtra23_IsADwarfMan_WithItsLookFieldsAndBakedName()
    {
        using var files = RealTextureData.Open();
        var resolver = new DisplayResolver(DisplayTables.Load(files), files);

        // Measured: race 3 (Dwarf), sex 0, skin 1, face 1, hair style 6, hair colour 3, facial hair 2; the bake name is a
        // 32-hex-digit file name ending in ".blp", resolved under Textures\BakedNpcTextures.
        DisplayExtra extra = Assert.IsType<DisplayExtra>(resolver.ResolveCreature(115).Extra);
        Assert.Equal((3, 0, 1, 1, 6, 3, 2), (extra.Race, extra.Sex, extra.Skin, extra.Face, extra.HairStyle, extra.HairColor, extra.FacialHair));
        Assert.Equal(@"Textures\BakedNpcTextures\973e54e79012eea3f2658f2897e681d9.blp", extra.BakedTexture);
        Assert.True(files.Exists(extra.BakedTexture!));
    }
}
