using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Display;
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
