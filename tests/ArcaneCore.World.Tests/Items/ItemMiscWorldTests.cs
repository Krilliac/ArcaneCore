using System.Buffers.Binary;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>The item name query and the equip-to-slot opcode over loopback (vmangos ItemHandler.cpp:92-106, 1022-1047).</summary>
public sealed class ItemMiscWorldTests
{
    private const string Account = "ITEMSMISC";
    private const string Name = "Reader";

    [Fact]
    public async Task ItemNameQuery_KnownEntryAnswered_UnknownEntryIgnored()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, InventoryType = 21 });
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);
            await client.SendAsync(WorldOpcode.CmsgItemNameQuery, [0x3F, 0x42, 0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // 999999: no reply
            await client.SendAsync(WorldOpcode.CmsgItemNameQuery, [25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            byte[] reply = await client.ReadUntilAsync(WorldOpcode.SmsgItemNameQueryResponse);
            Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
            Assert.Equal("Worn Shortsword", System.Text.Encoding.UTF8.GetString(reply, 4, reply.Length - 5));
        }
    }
}
