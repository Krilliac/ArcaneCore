using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Character state is saved when a player leaves the world, and field changes made on the
/// world thread reach the right clients as values updates.
/// </summary>
public sealed class PersistenceAndValuesTests
{
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;

    [Fact]
    public async Task Disconnect_SavesPositionAndPlayedTime()
    {
        await using var host = WorldTestHost.Start();
        WorldTestClient client = await host.EnterWorldAsync("SAVER", "Saver");

        await client.SendAsync(WorldOpcode.MsgMoveHeartbeat, VisibilityTests.BuildMovement(StartX + 25, StartY - 10, 84.0f, 1.25f));
        await host.WaitForWorldAsync(
            () => host.World.GetMap(0).FindPlayer(ObjectGuid.Player(1))?.X == StartX + 25, "heartbeat applied");
        await client.DisposeAsync();

        await WorldTestHost.WaitForAsync(() => host.Characters.SaveCount > 0, "state saved");
        CharacterRecord saved = (await host.Characters.GetByIdAsync(1))!;
        Assert.Equal(StartX + 25, saved.X);
        Assert.Equal(StartY - 10, saved.Y);
        Assert.Equal(84.0f, saved.Z);
        Assert.Equal(1.25f, saved.Orientation);
        Assert.True(saved.PlayedTime >= 1); // clears the first-login flag
    }

    [Fact]
    public async Task FieldChanges_ReachOwnerAndObservers_PrivateFieldsOnlyTheOwner()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("OWNER", "Owner");
        await using WorldTestClient b = await host.EnterWorldAsync("WATCHER", "Watcher");
        await a.ReadUpdateAsync(); // B created for A
        await b.ReadUpdateAsync(); // A created for B

        // On the world thread: change A's health (public) and XP (private, vmangos UF_FLAG_PRIVATE).
        await host.OnWorldAsync(() =>
        {
            Player owner = host.World.GetMap(0).FindPlayer(ObjectGuid.Player(1))!;
            owner.Health = 17;
            owner.SetUInt32(UpdateFields.PlayerXp, 123);
        });

        // Owner gets both fields in one values block.
        byte[] ownerBody = await a.ReadUpdateAsync();
        Dictionary<int, uint> ownerFields = ReadValuesBlock(ownerBody, guid: 1);
        Assert.Equal(17u, ownerFields[UpdateFields.UnitFieldHealth]);
        Assert.Equal(123u, ownerFields[UpdateFields.PlayerXp]);
        Assert.Equal(2, ownerFields.Count);

        // The observer only sees the public field.
        byte[] watcherBody = await b.ReadUpdateAsync();
        Dictionary<int, uint> watcherFields = ReadValuesBlock(watcherBody, guid: 1);
        Assert.Equal(17u, watcherFields[UpdateFields.UnitFieldHealth]);
        Assert.False(watcherFields.ContainsKey(UpdateFields.PlayerXp));
        Assert.Single(watcherFields);
    }

    /// <summary>Parse a single-block SMSG_UPDATE_OBJECT body holding one values block.</summary>
    private static Dictionary<int, uint> ReadValuesBlock(byte[] body, byte guid)
    {
        var reader = new PacketReader(body);
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadByte()); // has transport
        Assert.Equal((byte)ObjectUpdateType.Values, reader.ReadByte());
        Assert.Equal(guid, reader.ReadPackedGuid());

        int blocks = reader.ReadByte();
        uint[] mask = new uint[blocks];
        for (int i = 0; i < blocks; i++)
        {
            mask[i] = reader.ReadUInt32();
        }

        var fields = new Dictionary<int, uint>();
        for (int index = 0; index < blocks * 32; index++)
        {
            if ((mask[index >> 5] & (1u << (index & 31))) != 0)
            {
                fields[index] = reader.ReadUInt32();
            }
        }

        Assert.Equal(0, reader.Remaining);
        return fields;
    }
}
