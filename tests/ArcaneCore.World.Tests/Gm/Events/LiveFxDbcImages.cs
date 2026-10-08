using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.World.Tests.Gm.Events;

/// <summary>
/// Synthetic build-5875 client-effect DBC images (WDBC header, four-byte fields, NUL-separated UTF-8 string block) and a
/// directory of them for <c>World:GmCommands:LiveFxDbcDirectory</c>. Field numbers follow the readers' cited layouts, written
/// here independently: a column the reader takes from the wrong field reads a different value.
/// </summary>
public static class LiveFxDbcImages
{
    /// <summary>A WDBC image of <paramref name="fields"/> columns; each cell is a uint, an int, a float or a string (into the block).</summary>
    public static byte[] Build(int fields, params object?[][] rows)
    {
        var strings = new MemoryStream();
        strings.WriteByte(0); // offset 0 is the empty string
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal) { [string.Empty] = 0 };
        byte[] records = new byte[rows.Length * fields * 4];
        for (int row = 0; row < rows.Length; row++)
        {
            for (int field = 0; field < fields; field++)
            {
                object? cell = field < rows[row].Length ? rows[row][field] : null;
                uint value = cell switch
                {
                    null => 0,
                    uint u => u,
                    int i => unchecked((uint)i),
                    float f => BitConverter.SingleToUInt32Bits(f),
                    string s => Offset(s),
                    _ => throw new ArgumentException($"unsupported cell {cell.GetType()}"),
                };
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

        uint Offset(string text)
        {
            if (!offsets.TryGetValue(text, out uint offset))
            {
                offset = (uint)strings.Length;
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                strings.Write(bytes);
                strings.WriteByte(0);
                offsets[text] = offset;
            }

            return offset;
        }
    }

    /// <summary>A SoundEntries row: id 0, type 1, name 2, files 3-12, directory 23 (29 fields).</summary>
    public static object?[] Sound(uint id, int type, string name, string directory, params string[] files)
    {
        var row = new object?[29];
        row[0] = id;
        row[1] = type;
        row[2] = name;
        for (int i = 0; i < files.Length; i++)
        {
            row[3 + i] = files[i];
            row[13 + i] = 1; // frequencies, must not be read as anything else
        }

        row[23] = directory;
        row[24] = 1f;
        return row;
    }

    /// <summary>
    /// A directory holding all six client-effect DBCs with a few known rows (sounds 8440, 8574, 3439 and 25
    /// "Bulk_n" sounds 9000-9024; zone music 1; cinematic 2; kit 7 with effect 11; WorldStateUI row 126 showing fields 2313 and 2317).
    /// </summary>
    public static string WriteDirectory(bool withSounds = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcanecore-livefx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withSounds)
        {
            var sounds = new List<object?[]>
            {
                Sound(8440, 28, "Darkmoon_Faire_Music", "Sound\\Music\\WorldEvents", "DarkMoonFaire_2.mp3", "DarkMoonFaire_1.mp3"),
                Sound(8574, 1, "CrowdCheerHorde2", "Sound\\Spells", "CrowdCheerHorde2.wav"),
                Sound(3439, 25, "HornGoober", "Sound\\Doodad", "HornGoober.wav"),
            };
            for (uint i = 0; i < 25; i++)
            {
                sounds.Add(Sound(9000 + i, 1, "Bulk_" + i, "Sound\\Bulk", "bulk.wav"));
            }

            File.WriteAllBytes(Path.Combine(dir, "SoundEntries.dbc"), Build(29, [.. sounds]));
        }

        // ZoneMusic: id 0, name 1, silence min 2-3, max 4-5, sounds 6-7.
        File.WriteAllBytes(Path.Combine(dir, "ZoneMusic.dbc"), Build(8, new object?[] { 1u, "ZoneMusicDarkmoon", 60000, 60000, 180000, 180000, 8440u, 3439u }));
        // CinematicSequences: id 0, sound 1, cameras 2-9.
        File.WriteAllBytes(Path.Combine(dir, "CinematicSequences.dbc"), Build(10, new object?[] { 2u, 8574u, 41u, 0u, 42u }));
        // SpellVisualKit: id 0, kit type 1, anim 2, effects 3-12, sound 13.
        var kit = new object?[35];
        kit[0] = 7u;
        kit[1] = 3;
        kit[2] = 54;
        kit[3] = 11;   // head
        kit[4] = -1;   // chest: none
        kit[13] = 8574;
        kit[14] = 99;  // shake id, not the sound
        File.WriteAllBytes(Path.Combine(dir, "SpellVisualKit.dbc"), Build(35, kit));
        // SpellVisualEffectName: id 0, name 1, file 2.
        File.WriteAllBytes(Path.Combine(dir, "SpellVisualEffectName.dbc"), Build(5, new object?[] { 11u, "HolyGlow", "Spells\\Holy_Glow.mdx", 0, 1f }));
        // WorldStateUI: id 0, map 1, area 2, icon 3, text 4, tooltip 13, faction 22, state variable 23, type 24, extended UI 35-38.
        var state = new object?[39];
        state[0] = 126u;
        state[1] = 1;
        state[2] = 1377;
        state[3] = "Interface\\TargetingFrame\\UI-PVP-Alliance";
        state[4] = "%2313w/%2317w";
        state[13] = "Alliance Silithyst\nCollected";
        state[22] = -1;  // faction, not a field
        state[24] = 1;
        File.WriteAllBytes(Path.Combine(dir, "WorldStateUI.dbc"), Build(39, state));
        return dir;
    }
}
