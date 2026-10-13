using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// /who lists only players who are in the world (vmangos WhoListClientQueryTask, MiscHandler.cpp: the
/// <c>!IsInWorld()</c> skip). A far teleport removes the player from the map until MSG_MOVE_WORLDPORT_ACK,
/// but the player stays in the online registry for that whole load.
/// </summary>
public sealed class WhoListTests
{
    [Fact]
    public async Task Who_OmitsAPlayerWhoIsInAFarTeleport()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient asker = await host.EnterWorldAsync("ASKER", "Asker");
        await using WorldTestClient traveler = await host.EnterWorldAsync("TRAVELER", "Traveler", AccountSecurity.GameMaster);
        await asker.CollectAsync();
        await traveler.CollectAsync();

        Assert.Equal(["Asker", "Traveler"], await WhoAsync(asker));

        await traveler.SendChatAsync(ChatType.Say, Language.Common, ".tele cross");
        await traveler.ReadUntilAsync(WorldOpcode.SmsgTransferPending);
        await traveler.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Traveler")!.Map is null, "traveler has left the map");

        Assert.Equal(["Asker"], await WhoAsync(asker));
    }

    private static async Task<List<string>> WhoAsync(WorldTestClient client)
    {
        var who = new PacketWriter(64);
        who.WriteUInt32(0);
        who.WriteUInt32(100);
        who.WriteCString(string.Empty);
        who.WriteCString(string.Empty);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0);
        who.WriteUInt32(0);

        await client.SendAsync(WorldOpcode.CmsgWho, who.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgWho));
        uint listed = reader.ReadUInt32();
        reader.ReadUInt32();
        var names = new List<string>();
        for (int i = 0; i < listed; i++)
        {
            names.Add(reader.ReadCString());
            reader.ReadCString();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
