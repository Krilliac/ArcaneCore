using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.ClientEffects;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Tests.Skills;
using Xunit;

namespace ArcaneCore.Data.Tests.ClientEffects;

/// <summary>
/// The build-5875 client-effect DBC readers (SoundEntries, ZoneMusic, CinematicSequences, SpellVisualKit,
/// SpellVisualEffectName, WorldStateUI): each column comes from its cited field, other layouts and zero or repeated ids
/// are refused, and the real client files (ARCANECORE_TEST_DBC_DIR) load with their known row counts.
/// </summary>
public sealed class ClientEffectDbcReaderTests
{
    [Fact]
    public void SoundEntries_DecodesIdTypeNameFilesAndDirectory()
    {
        var row = new object?[29];
        row[0] = 8440u;
        row[1] = 28;
        row[2] = "Darkmoon_Faire_Music";
        row[3] = "a.mp3";
        row[5] = "c.mp3";        // file 1 empty: skipped, order kept
        row[13] = 7;             // a frequency, not a file
        row[23] = "Sound\\Music";
        row[24] = 1f;

        DbcTable<SoundEntry> table = SoundEntriesDbcReader.Read(DbcFile.Parse(Image(29, row)));

        SoundEntry sound = Assert.Single(table.Rows);
        Assert.Equal((8440u, 28, "Darkmoon_Faire_Music", "Sound\\Music"), (sound.Id, sound.SoundType, sound.Name, sound.Directory));
        Assert.Equal(["a.mp3", "c.mp3"], sound.Files);
        Assert.True(table.Contains(8440));
        Assert.False(table.Contains(8441));
        Assert.Same(sound, table.Find(8440));
        Assert.Equal("SoundEntries.dbc", table.FileName);
    }

    [Fact]
    public void ZoneMusic_CinematicSequences_SpellVisualKit_AndEffectNames_DecodeTheirColumns()
    {
        ZoneMusicEntry music = Assert.Single(ZoneMusicDbcReader.Read(DbcFile.Parse(Image(8, new object?[] { 5u, "Set", 1, 2, 3, 4, 100u, 200u }))).Rows);
        Assert.Equal(new ZoneMusicEntry(5, "Set", 100, 200), music);

        CinematicSequence cinematic = Assert.Single(CinematicSequencesDbcReader.Read(DbcFile.Parse(Image(10, new object?[] { 2u, 77u, 0u, 41u, 0u, 0u, 0u, 0u, 0u, 43u }))).Rows);
        Assert.Equal((2u, 77u), (cinematic.Id, cinematic.SoundId));
        Assert.Equal([41u, 43u], cinematic.Cameras);

        var kit = new object?[35];
        kit[0] = 9u;
        kit[1] = 2;
        kit[2] = 54;
        kit[3] = 11;
        kit[4] = -1;
        kit[12] = 13;  // world effect
        kit[13] = 300;
        kit[14] = 5;   // shake, not the sound
        SpellVisualKitEntry entry = Assert.Single(SpellVisualKitDbcReader.Read(DbcFile.Parse(Image(35, kit))).Rows);
        Assert.Equal((9u, 2, 54, 300u), (entry.Id, entry.KitType, entry.AnimId, entry.SoundId));
        Assert.Equal([11u, 13u], entry.Effects);

        SpellVisualEffectName effect = Assert.Single(SpellVisualEffectNameDbcReader.Read(DbcFile.Parse(Image(5, new object?[] { 11u, "HolyGlow", "x.mdx", 0, 1f }))).Rows);
        Assert.Equal(new SpellVisualEffectName(11, "HolyGlow", "x.mdx"), effect);
    }

    [Fact]
    public void WorldStateUI_DecodesTheEnUsStringsAndTheStateVariable()
    {
        var row = new object?[39];
        row[0] = 5u;
        row[1] = 489;
        row[2] = 3277;
        row[3] = "icon";
        row[4] = "enUS text";
        row[5] = "koKR text";
        row[13] = "tooltip";
        row[22] = 469;   // faction, not the variable
        row[23] = 2313u;
        row[24] = 1;
        row[35] = "CAPTUREPOINT";
        row[36] = 2427u;
        row[38] = 2428u;
        WorldStateUIEntry state = Assert.Single(WorldStateUIDbcReader.Read(DbcFile.Parse(Image(39, row))).Rows);
        Assert.Equal(
            (5u, 489, 3277, "icon", "enUS text", "tooltip", 2313u, 1, "CAPTUREPOINT"),
            (state.Id, state.MapId, state.AreaId, state.Icon, state.Text, state.Tooltip, state.StateVariable, state.Type, state.ExtendedUI));
        Assert.Equal([2427u, 2428u], state.ExtendedUIStateVariables);
    }

    public static TheoryData<string, int> Layouts => new()
    {
        { SoundEntriesDbcReader.FileName, 29 },
        { ZoneMusicDbcReader.FileName, 8 },
        { CinematicSequencesDbcReader.FileName, 10 },
        { SpellVisualKitDbcReader.FileName, 35 },
        { SpellVisualEffectNameDbcReader.FileName, 5 },
        { WorldStateUIDbcReader.FileName, 39 },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void OtherLayouts_ZeroIds_AndRepeatedIds_AreRefused(string file, int fields)
    {
        var one = new object?[fields];
        one[0] = 3u;
        Assert.Equal(1, Read(file, Image(fields, one)));
        Assert.Throws<InvalidDataException>(() => Read(file, Image(fields - 1, new object?[fields - 1])));
        Assert.Throws<InvalidDataException>(() => Read(file, Image(fields + 1, new object?[fields + 1])));
        Assert.Throws<InvalidDataException>(() => Read(file, Image(fields, new object?[fields])));
        Assert.Throws<InvalidDataException>(() => Read(file, Image(fields, one, one)));
    }

    [RealDbcFact]
    public void TheRealClientFiles_LoadWithTheirKnownRowCounts()
    {
        // Reports Skipped (naming the variable) when ARCANECORE_TEST_DBC_DIR is unset, never a silent pass.
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        Assert.Equal(4623, Read(SoundEntriesDbcReader.FileName, File.ReadAllBytes(Path.Combine(dir, SoundEntriesDbcReader.FileName))));
        Assert.Equal(99, Read(ZoneMusicDbcReader.FileName, File.ReadAllBytes(Path.Combine(dir, ZoneMusicDbcReader.FileName))));
        Assert.Equal(10, Read(CinematicSequencesDbcReader.FileName, File.ReadAllBytes(Path.Combine(dir, CinematicSequencesDbcReader.FileName))));
        Assert.Equal(1772, Read(SpellVisualKitDbcReader.FileName, File.ReadAllBytes(Path.Combine(dir, SpellVisualKitDbcReader.FileName))));
        Assert.Equal(775, Read(SpellVisualEffectNameDbcReader.FileName, File.ReadAllBytes(Path.Combine(dir, SpellVisualEffectNameDbcReader.FileName))));
        Assert.Equal(20, Read(WorldStateUIDbcReader.FileName, File.ReadAllBytes(Path.Combine(dir, WorldStateUIDbcReader.FileName))));

        DbcTable<SoundEntry> sounds = SoundEntriesDbcReader.Load(Path.Combine(dir, SoundEntriesDbcReader.FileName));
        Assert.Equal(("HornGoober", 25), (sounds.Find(3439)!.Name, sounds.Find(3439)!.SoundType));
        WorldStateUIEntry capture = WorldStateUIDbcReader.Load(Path.Combine(dir, WorldStateUIDbcReader.FileName)).Find(138)!;
        Assert.Equal(("CAPTUREPOINT", 2426u), (capture.ExtendedUI, capture.StateVariable));
        Assert.Equal([2427u, 2428u], capture.ExtendedUIStateVariables);
    }

    private static int Read(string file, byte[] image)
    {
        DbcFile dbc = DbcFile.Parse(image);
        return file switch
        {
            SoundEntriesDbcReader.FileName => SoundEntriesDbcReader.Read(dbc).Count,
            ZoneMusicDbcReader.FileName => ZoneMusicDbcReader.Read(dbc).Count,
            CinematicSequencesDbcReader.FileName => CinematicSequencesDbcReader.Read(dbc).Count,
            SpellVisualKitDbcReader.FileName => SpellVisualKitDbcReader.Read(dbc).Count,
            SpellVisualEffectNameDbcReader.FileName => SpellVisualEffectNameDbcReader.Read(dbc).Count,
            WorldStateUIDbcReader.FileName => WorldStateUIDbcReader.Read(dbc).Count,
            _ => throw new ArgumentException(file),
        };
    }

    /// <summary>A WDBC image; each cell is a uint, int, float or string (written to the string block).</summary>
    private static byte[] Image(int fields, params object?[][] rows)
    {
        var strings = new MemoryStream();
        strings.WriteByte(0);
        byte[] records = new byte[rows.Length * fields * 4];
        for (int row = 0; row < rows.Length; row++)
        {
            for (int field = 0; field < fields; field++)
            {
                object? cell = field < rows[row].Length ? rows[row][field] : null;
                uint value;
                if (cell is string text)
                {
                    value = (uint)strings.Length;
                    strings.Write(Encoding.UTF8.GetBytes(text));
                    strings.WriteByte(0);
                }
                else
                {
                    value = cell switch
                    {
                        null => 0,
                        uint u => u,
                        int i => unchecked((uint)i),
                        float f => BitConverter.SingleToUInt32Bits(f),
                        _ => throw new ArgumentException($"unsupported cell {cell.GetType()}"),
                    };
                }

                BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(((row * fields) + field) * 4), value);
            }
        }

        byte[] block = strings.ToArray();
        byte[] image = new byte[20 + records.Length + block.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        records.CopyTo(image, 20);
        block.CopyTo(image, 20 + records.Length);
        return image;
    }
}
