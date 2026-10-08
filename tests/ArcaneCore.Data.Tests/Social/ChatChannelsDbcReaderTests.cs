using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Data.Tests.Social;

/// <summary>
/// ChatChannels.dbc of build 5875 (vmangos DBCfmt.h "nixssssssssxxxxxxxxxx": id, flags, faction group, eight locale name
/// patterns, then the mask and the shortcuts, not read). The data is synthetic; the developer's own client file is read only
/// when <c>ARCANECORE_CLIENT_DBC_DIR</c> points at it.
/// </summary>
public sealed class ChatChannelsDbcReaderTests
{
    [Fact]
    public void SyntheticRows_DecodeIdFlagsFactionAndEveryLocalePattern_InFileOrder()
    {
        IReadOnlyList<ChatChannelRow> rows = ChatChannelsDbcReader.Read(DbcFile.Parse(Image(ChatChannelsDbcReader.FieldCount,
            (1, 0x3, 0, ["General - %s", "", "Allgemein - %s", "", "", "", "", ""]),
            (24, 0x0, 0, ["LookingForGroup", "", "", "", "", "", "", ""]))));

        Assert.Equal([1u, 24u], rows.Select(r => r.Id));
        Assert.Equal((0x3u, 0u), (rows[0].Flags, rows[0].FactionGroup));
        Assert.Equal(["General - %s", "", "Allgemein - %s", "", "", "", "", ""], rows[0].Patterns);
        Assert.Equal("LookingForGroup", rows[1].Patterns[0]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(37)] // the 2.4.3 layout
    public void AnotherBuildsLayout_IsRefused(int fields)
        => Assert.Throws<InvalidDataException>(() => ChatChannelsDbcReader.Read(DbcFile.Parse(Image(fields, (1, 0, 0, ["General - %s"])))));

    [Fact]
    public void ZeroOrDuplicateIds_AreRefused()
    {
        Assert.Throws<InvalidDataException>(() => ChatChannelsDbcReader.Read(DbcFile.Parse(Image(21, (0, 0, 0, ["X"])))));
        Assert.Throws<InvalidDataException>(() => ChatChannelsDbcReader.Read(DbcFile.Parse(Image(21, (2, 0, 0, ["A"]), (2, 0, 0, ["B"])))));
    }

    [Fact]
    public void TheDevelopersOwnClientFile_HasTheSixRowsTheServerTranscribes()
    {
        if (Environment.GetEnvironmentVariable("ARCANECORE_CLIENT_DBC_DIR") is not { Length: > 0 } directory
            || !File.Exists(Path.Combine(directory, "ChatChannels.dbc")))
        {
            return; // no client data on this machine (none is shipped)
        }

        IReadOnlyList<ChatChannelRow> rows = ChatChannelsDbcReader.Load(Path.Combine(directory, "ChatChannels.dbc"));
        Assert.Equal([(1u, 0x3u), (2u, 0x3Bu), (22u, 0x10003u), (23u, 0x10004u), (24u, 0x0u), (25u, 0x20032u)], rows.Select(r => (r.Id, r.Flags)));
    }

    /// <summary>A WDBC image whose rows put the given strings into fields 3 onwards (the rest of a row is zero).</summary>
    internal static byte[] Image(int fields, params (uint Id, uint Flags, uint Faction, string[] Strings)[] rows)
    {
        var strings = new MemoryStream();
        strings.WriteByte(0); // offset 0 is the empty string
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal) { [string.Empty] = 0 };
        uint Offset(string value)
        {
            if (!offsets.TryGetValue(value, out uint at))
            {
                at = (uint)strings.Length;
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                strings.Write(bytes);
                strings.WriteByte(0);
                offsets[value] = at;
            }

            return at;
        }

        var records = new uint[rows.Length][];
        for (int r = 0; r < rows.Length; r++)
        {
            records[r] = new uint[fields];
            records[r][0] = rows[r].Id;
            records[r][1] = rows[r].Flags;
            records[r][2] = rows[r].Faction;
            for (int i = 0; i < rows[r].Strings.Length && 3 + i < fields; i++)
            {
                records[r][3 + i] = Offset(rows[r].Strings[i]);
            }
        }

        byte[] block = strings.ToArray();
        byte[] image = new byte[20 + rows.Length * fields * 4 + block.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        for (int r = 0; r < rows.Length; r++)
        {
            for (int f = 0; f < fields; f++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (r * fields + f) * 4), records[r][f]);
            }
        }

        block.CopyTo(image, 20 + rows.Length * fields * 4);
        return image;
    }
}
