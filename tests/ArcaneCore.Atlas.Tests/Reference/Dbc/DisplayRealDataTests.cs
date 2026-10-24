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
        Assert.Equal(10, extra.Equipment.Count);
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
}
