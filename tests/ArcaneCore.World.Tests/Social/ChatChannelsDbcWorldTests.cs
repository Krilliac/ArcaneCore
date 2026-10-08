using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Channels;
using ArcaneCore.Protocol;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// World:Chat:ChatChannelsDbcPath: the built-in channels come from the client's own ChatChannels.dbc, every locale included
/// (vmangos loads sChatChannelsStore and matches every locale's pattern, DBCStores.cpp:531-552). Synthetic file.
/// </summary>
public sealed class ChatChannelsDbcWorldTests
{
    [Fact]
    public async Task AGermanGeneralChannel_IsTheBuiltInChannel_WhenTheClientFileIsConfigured()
    {
        string file = Path.Combine(Path.GetTempPath(), "arcane-chatchannels-" + Guid.NewGuid().ToString("N") + ".dbc");
        await File.WriteAllBytesAsync(file, Image(
            (1, 0x3, ["General - %s", "", "Allgemein - %s"]),
            (2, 0x3B, ["Trade - %s", "", "Handel - %s"])));
        try
        {
            await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["World:Chat:ChatChannelsDbcPath"] = file }).Build()));
            SocialFeature social = host.WorldServices.GetRequiredService<SocialFeature>();
            Assert.True(social.Context.ChannelCatalog.FromClientData);

            await using WorldTestClient client = await host.EnterWorldAsync("DEUTSCH", "Deutsch");
            await client.CollectAsync();
            var join = new PacketWriter(64);
            join.WriteCString("Allgemein - Wald von Elwynn");
            join.WriteCString(string.Empty);
            await client.SendAsync(WorldOpcode.CmsgJoinChannel, join.ToArray());

            var notify = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgChannelNotify));
            Assert.Equal((byte)ChatNotify.YouJoined, notify.ReadByte());
            Assert.Equal("Allgemein - Wald von Elwynn", notify.ReadCString());
            Assert.Equal(0x18u, notify.ReadUInt32()); // GENERAL | NOT_LFG: a built-in channel, not CUSTOM (0x01)
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task WithoutAFile_TheTranscribedEnglishRowsAreUsed()
    {
        await using var host = WorldTestHost.Start();
        Assert.Same(ChatChannelCatalog.Builtin, host.WorldServices.GetRequiredService<SocialFeature>().Context.ChannelCatalog);
    }

    private static byte[] Image(params (uint Id, uint Flags, string[] Patterns)[] rows)
    {
        const int Fields = 21;
        var block = new List<byte> { 0 };
        var records = new uint[rows.Length * Fields];
        for (int r = 0; r < rows.Length; r++)
        {
            records[r * Fields] = rows[r].Id;
            records[r * Fields + 1] = rows[r].Flags;
            for (int i = 0; i < rows[r].Patterns.Length; i++)
            {
                if (rows[r].Patterns[i].Length == 0)
                {
                    continue;
                }

                records[r * Fields + 3 + i] = (uint)block.Count;
                block.AddRange(Encoding.UTF8.GetBytes(rows[r].Patterns[i]));
                block.Add(0);
            }
        }

        byte[] image = new byte[20 + records.Length * 4 + block.Count];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), Fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), Fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Count);
        for (int i = 0; i < records.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + i * 4), records[i]);
        }

        block.CopyTo(image, 20 + records.Length * 4);
        return image;
    }
}
