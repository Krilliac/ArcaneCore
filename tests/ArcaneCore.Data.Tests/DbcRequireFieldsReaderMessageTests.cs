using System.Buffers.Binary;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Pets;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Items;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Reputation;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Characterisation of per-reader field-count failure wording (exact / one-of checks) that the DbcFile.Require* helpers
/// and the DbcFile namespace move must preserve. Complements <see cref="DbcFieldCountMessageTests"/>.
/// </summary>
public sealed class DbcRequireFieldsReaderMessageTests
{
    private static DbcFile Table(int fields)
    {
        int recordSize = fields * 4;
        byte[] image = new byte[20 + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)recordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        return DbcFile.Parse(image);
    }

    private static string Message(Action action) => Assert.Throws<InvalidDataException>(action).Message;

    [Fact]
    public void MapReader_MessageIncludesFieldCountAndRecordSize()
    {
        Assert.Equal("Map.dbc requires 42 four-byte fields, found 41/164", Message(() => MapDbcReader.Read(Table(41))));
        Assert.Equal("Map.dbc must contain both continent ids 0 and 1", Message(() => MapDbcReader.Read(Table(MapDbcReader.FieldCount))));
    }

    [Fact]
    public void AreaTableReader_MessageIncludesFieldCountAndRecordSize()
    {
        Assert.Equal("AreaTable.dbc requires 25 four-byte fields, found 26/104", Message(() => AreaTableDbcReader.Read(Table(26))));
        Assert.Empty(AreaTableDbcReader.Read(Table(AreaTableDbcReader.FieldCount)));
    }

    [Fact]
    public void CharacterAppearanceReaders_Messages()
    {
        Assert.Equal("CharSections.dbc has 9 fields, expected 10", Message(() => CharacterAppearanceDbcReader.ReadSections(Table(9))));
        Assert.Equal("CharacterFacialHairStyles.dbc has 10 fields, expected 9", Message(() => CharacterAppearanceDbcReader.ReadFacialHairStyles(Table(10))));
    }

    [Fact]
    public void ItemSetAndWorldSafeLocsAndFamily_Messages()
    {
        Assert.Equal("ItemSet.dbc has 44 fields, expected 45", Message(() => ItemSetDbcReader.Build(Table(44))));
        Assert.Equal("build-5875 WorldSafeLocs.dbc requires 14 four-byte fields, found 13", Message(() => WorldSafeLocsDbcReader.Read(Table(13))));
        Assert.Equal("build-5875 CreatureFamily.dbc requires 18 four-byte fields, found 17", Message(() => CreatureFamilyDbcReader.ReadFoodMasks(Table(17))));
    }

    [Fact]
    public void FixedMessageReaders_DoNotIncludeCounts()
    {
        Assert.Equal("build-5875 Faction.dbc requires thirty-seven four-byte fields", Message(() => FactionDbcReader.Read(Table(36))));
        Assert.Equal("build-5875 FactionTemplate.dbc requires fourteen four-byte fields", Message(() => FactionTemplateDbcReader.Read(Table(15))));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(16)]
    public void SkillLineAbility_OneOfCheck_RejectsOtherCounts(int fields)
        => Assert.Equal("build-5875 SkillLineAbility.dbc requires 15 (or 14) four-byte fields",
            Message(() => NpcServiceDbcReaders.ReadSkillLineAbilityRecords(Table(fields))));

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    public void SkillLineAbility_OneOfCheck_AcceptsBothLayouts(int fields, bool hasTrainingPoints)
        => Assert.Equal(hasTrainingPoints, NpcServiceDbcReaders.ReadSkillLineAbilities(Table(fields)).HasTrainingPoints);
}
