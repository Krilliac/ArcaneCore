using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>SMSG_NPC_TEXT_UPDATE for a text built from broadcast_text rows (vmangos Query.cpp NpcTextUpdate layout).</summary>
public sealed class NpcTextUpdatePacketTests
{
    [Fact]
    public void ABroadcastTextWritesBothGendersTheLanguageAndThreeEmoteDelayPairs()
    {
        NpcTextOption first = new(1f, "Male line", "Female line", 7, 10, 1, 20, 2, 30, 3);
        NpcTextOption empty = new(0f, string.Empty, string.Empty, 0, 0, 0, 0, 0, 0, 0);
        var text = new NpcText { Id = 9001, Options = [first, empty, empty, empty, empty, empty, empty, empty] };
        var store = new NpcStore(NpcContent.Empty with { NpcTexts = [text] });

        var reader = new PacketReader(NpcPackets.NpcTextUpdate(9001, store.Text(9001)).ToArray());
        Assert.Equal(9001u, reader.ReadUInt32());
        Assert.Equal(1f, reader.ReadSingle());
        Assert.Equal("Male line", reader.ReadCString());
        Assert.Equal("Female line", reader.ReadCString());
        Assert.Equal(7u, reader.ReadUInt32());
        uint[] pairs = [reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()];
        Assert.Equal([10u, 1u, 20u, 2u, 30u, 3u], pairs);
    }

    [Fact]
    public void AnIdMissingFromBothTablesStillGetsTheDefaultGreeting()
    {
        var store = new NpcStore(NpcContent.Empty);
        var reader = new PacketReader(NpcPackets.NpcTextUpdate(424242, store.Text(424242)).ToArray());
        Assert.Equal(424242u, reader.ReadUInt32());
        Assert.Equal(0f, reader.ReadSingle());
        Assert.Equal(NpcPackets.DefaultGreeting, reader.ReadCString());
        Assert.Equal(NpcPackets.DefaultGreeting, reader.ReadCString());
    }
}
