using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Tests.Skills;
using ArcaneCore.Kernel.Characters;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Data.Tests.Characters;

/// <summary>
/// The CharSections.dbc and CharacterFacialHairStyles.dbc decoders and vmangos Player::ValidateAppearance (Player.cpp:326-357) over them. The
/// synthetic images are built from the vmangos field positions (DBCfmt.h:29-30), not from the reader's own constants.
/// </summary>
public sealed class CharacterAppearanceDbcReaderTests(ITestOutputHelper output)
{
    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + (records.Length * fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        return image;
    }

    /// <summary>CharSections "diiiiixxxi": [0] id, [1] race, [2] gender, [3] section, [4] variation, [5] colour, [6-8] textures, [9] flags.</summary>
    private static uint[] Section(uint id, uint race, uint gender, uint section, uint variation, uint color, uint flags = 0)
        => [id, race, gender, section, variation, color, 0, 0, 0, flags];

    /// <summary>CharacterFacialHairStyles "iiixxxxxx": [0] race, [1] gender, [2] variation, [3-8] geosets.</summary>
    private static uint[] Beard(uint race, uint gender, uint variation) => [race, gender, variation, 7, 7, 7, 7, 7, 7];

    /// <summary>A human male with skin 0-1, face 0 (both skins), hair 0 in colour 0-1, facial hair 0 in colour 0-1, beard style 0.</summary>
    private static CharacterAppearanceCatalog HumanMale(params uint[][] extra)
        => CharacterAppearanceDbcReader.Build(
            DbcFile.Parse(Image(10, [
                Section(1, 1, 0, 0, 0, 0), Section(2, 1, 0, 0, 0, 1),
                Section(3, 1, 0, 1, 0, 0), Section(4, 1, 0, 1, 0, 1),
                Section(5, 1, 0, 3, 0, 0), Section(6, 1, 0, 3, 0, 1),
                Section(7, 1, 0, 2, 0, 0), Section(8, 1, 0, 2, 0, 1),
                .. extra])),
            DbcFile.Parse(Image(9, Beard(1, 0, 0))));

    [Fact]
    public void EveryCheck_OfValidateAppearance_UsesItsOwnColumns()
    {
        CharacterAppearanceCatalog catalog = HumanMale();
        Assert.Equal((8, 1), (catalog.SectionCount, catalog.FacialHairStyleCount));

        Assert.True(catalog.IsValid(1, 0, new CharacterAppearance(0, 0, 0, 0, 0)));
        Assert.True(catalog.IsValid(1, 0, new CharacterAppearance(1, 0, 0, 1, 0)));
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(2, 0, 0, 0, 0)));   // no skin colour 2
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(0, 1, 0, 0, 0)));   // no face 1
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(0, 0, 1, 0, 0)));   // no hair style 1
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(0, 0, 0, 2, 0)));   // no hair colour 2
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(0, 0, 0, 0, 1)));   // no facial hair 1
        Assert.False(catalog.IsValid(1, 1, new CharacterAppearance(0, 0, 0, 0, 0)));   // no female rows at all
        Assert.False(catalog.IsValid(2, 0, new CharacterAppearance(0, 0, 0, 0, 0)));   // no orc rows
    }

    [Fact]
    public void AnUnavailableRow_IsNotAChoice_ButAnAvailableTwinIs()
    {
        CharacterAppearanceCatalog catalog = HumanMale(Section(9, 1, 0, 0, 0, 5, flags: 1));
        Assert.False(catalog.HasSection(1, CharSectionType.Skin, 0, 0, 5));
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(5, 0, 0, 0, 0)));

        // The real file lists some skins twice, once flagged unavailable: the available row wins (GetCharSectionEntry keeps looking).
        CharacterAppearanceCatalog twin = HumanMale(Section(9, 1, 0, 0, 0, 0, flags: 1));
        Assert.True(twin.IsValid(1, 0, new CharacterAppearance(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void TaurenAndMostWomen_SkipTheFacialHairSection_ButNotTheStyleRow()
    {
        // Tauren male and human female: skin, face, hair and a beard style row, no facial hair section.
        CharacterAppearanceCatalog catalog = CharacterAppearanceDbcReader.Build(
            DbcFile.Parse(Image(10,
                Section(1, 6, 0, 0, 0, 0), Section(2, 6, 0, 1, 0, 0), Section(3, 6, 0, 3, 0, 0),
                Section(4, 1, 1, 0, 0, 0), Section(5, 1, 1, 1, 0, 0), Section(6, 1, 1, 3, 0, 0),
                Section(7, 4, 1, 0, 0, 0), Section(8, 4, 1, 1, 0, 0), Section(9, 4, 1, 3, 0, 0))),
            DbcFile.Parse(Image(9, Beard(6, 0, 0), Beard(1, 1, 0), Beard(4, 1, 0))));

        Assert.True(catalog.IsValid(6, 0, new CharacterAppearance(0, 0, 0, 0, 0)));
        Assert.True(catalog.IsValid(1, 1, new CharacterAppearance(0, 0, 0, 0, 0)));
        Assert.False(catalog.IsValid(6, 0, new CharacterAppearance(0, 0, 0, 0, 3)));   // the style row is still required
        // A night elf woman has facial hair sections ("markings"): without one she is refused.
        Assert.False(catalog.IsValid(4, 1, new CharacterAppearance(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void NonPlayableRaces_AndOutOfRangeValues_AreSkippedAtLoad()
    {
        CharacterAppearanceCatalog catalog = CharacterAppearanceDbcReader.Build(
            DbcFile.Parse(Image(10, Section(1, 9, 0, 0, 0, 0), Section(2, 257, 0, 0, 0, 0), Section(3, 1, 0, 0, 0, 256), Section(4, 1, 0, 0, 0, 0))),
            DbcFile.Parse(Image(9, Beard(9, 0, 0), Beard(1, 0, 300), Beard(1, 0, 0))));

        // Race 257 and colour 256 would alias race 1 and colour 0 if cast: only the one real row is left of each file.
        Assert.Equal((1, 1), (catalog.SectionCount, catalog.FacialHairStyleCount));
        Assert.False(catalog.HasSection(9, CharSectionType.Skin, 0, 0, 0));
        Assert.True(catalog.HasSection(1, CharSectionType.Skin, 0, 0, 0));
        Assert.False(catalog.HasFacialHairStyle(1, 0, 44));   // 300 & 0xFF
    }

    [Fact]
    public void AnotherLayout_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceDbcReader.ReadSections(DbcFile.Parse(Image(9, Beard(1, 0, 0)))));
        Assert.Throws<InvalidDataException>(() => CharacterAppearanceDbcReader.ReadFacialHairStyles(DbcFile.Parse(Image(10, Section(1, 1, 0, 0, 0, 0)))));
    }

    [Fact]
    public void TheEmptyCatalog_IsEmpty()
    {
        Assert.True(CharacterAppearanceCatalog.Empty.IsEmpty);
        Assert.False(HumanMale().IsEmpty);
    }

    [RealDbcFact]
    public void RealDbcProbe_TheDefaultLooksOfEveryRaceAndGender_AreValid_AndNonsenseIsNot()
    {
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        CharacterAppearanceCatalog catalog = CharacterAppearanceDbcReader.Load(
            Path.Combine(dir, "CharSections.dbc"), Path.Combine(dir, "CharacterFacialHairStyles.dbc"));
        output.WriteLine($"CharSections {catalog.SectionCount} available playable rows, CharacterFacialHairStyles {catalog.FacialHairStyleCount} rows");

        // Build 5875: 3671 CharSections rows, 68 of them flagged unavailable; the other 3603 are all of races 1-8. 136 facial hair styles.
        Assert.Equal(3603, catalog.SectionCount);
        Assert.Equal(136, catalog.FacialHairStyleCount);

        int probes = 0;
        for (byte race = 1; race <= 8; race++)
        {
            for (byte gender = 0; gender <= 1; gender++)
            {
                // The client's default choice (all zero) is what the playerbots and the mock client send.
                Assert.True(catalog.IsValid(race, gender, new CharacterAppearance(0, 0, 0, 0, 0)), $"race {race} gender {gender}");
                Assert.False(catalog.IsValid(race, gender, new CharacterAppearance(200, 0, 0, 0, 0)), $"race {race} gender {gender} skin 200");
                Assert.False(catalog.IsValid(race, gender, new CharacterAppearance(0, 0, 200, 0, 0)), $"race {race} gender {gender} hair 200");
                probes += 3;
            }
        }

        // Human male: hair style 11 exists in 1.12.1 (12 styles), a beard style 8 too (9 styles), style 9 does not.
        Assert.True(catalog.IsValid(1, 0, new CharacterAppearance(0, 0, 11, 0, 8)));
        Assert.False(catalog.IsValid(1, 0, new CharacterAppearance(0, 0, 0, 0, 9)));
        probes += 2;
        output.WriteLine($"{probes} probes ran / 0 skipped");
    }
}
