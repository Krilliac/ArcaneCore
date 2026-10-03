using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.GridTerrain;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>
/// A ghost at a dungeon door over a real socket (vmangos HandleAreaTriggerOpcode, MiscHandler.cpp:712-756): the test world's Deadmines
/// (map 36, entrance trigger 78 near the human start) takes a ghost only when its corpse is in the Deadmines.
/// </summary>
public sealed class GhostTravelWorldTests
{
    private static byte[] AreaTrigger(uint id)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, id);
        return bytes;
    }

    private static async Task<WorldTestClient> GhostAtTheDoorAsync(WorldTestHost host, string account, string name, uint corpseMap)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name);
        await client.CollectAsync();
        await host.PlaceAsync(name, -8962f, -130f, 84f); // inside the Deadmines trigger box
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Level = 10; // the Deadmines trigger asks for level 10
            PlayerLife.ApplyGhostState(player);
            player.Map!.Combat.RestoreGhost(player, new CorpseSnapshot(corpseMap, player.X, player.Y, player.Z, 0f, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, (byte)CorpseType.ResurrectablePve));
        });
        await client.CollectAsync();
        return client;
    }

    [Fact]
    public async Task AGhostWhoseCorpseIsOutsideTheDungeon_IsToldItCannotEnter_AndStays()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await GhostAtTheDoorAsync(host, "GTRAVEL1", "Travelone", corpseMap: 0);

        await client.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));

        byte[] message = await client.ReadUntilAsync(WorldOpcode.SmsgAreaTriggerMessage);
        string text = "You cannot enter Deadmines while in ghost form.";
        Assert.Equal((uint)(text.Length + 1), BinaryPrimitives.ReadUInt32LittleEndian(message));
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(message, 4, text.Length));
        Assert.Equal(0u, await host.PlayerStateAsync("Travelone", p => p.MapId));
        Assert.False(await host.PlayerStateAsync("Travelone", p => p.IsAlive));
    }

    [Fact]
    public async Task AGhostWhoseCorpseIsInTheDungeon_EntersItAndIsResurrectedAtHalfHealth()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await GhostAtTheDoorAsync(host, "GTRAVEL2", "Traveltwo", corpseMap: 36);

        await client.SendAsync(WorldOpcode.CmsgAreatrigger, AreaTrigger(InMemoryMapDataStore.DeadminesTrigger));

        byte[] pending = await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending);
        Assert.Equal(36u, BinaryPrimitives.ReadUInt32LittleEndian(pending));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Traveltwo")!.IsAlive, "the ghost to be revived at the door");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Traveltwo")!;
            Assert.Equal(player.MaxHealth / 2, player.Health);
            Assert.Null(player.Combat.Corpse);
        });
    }
}
