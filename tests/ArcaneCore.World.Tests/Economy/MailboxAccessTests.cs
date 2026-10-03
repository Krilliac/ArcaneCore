using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Tests.GameObjects;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>
/// The default mailbox check against vmangos CheckMailBox (D:\refs\vmangos\src\game\Handlers\MailHandler.cpp:64-73 →
/// GetGameObjectIfCanInteractWith(GAMEOBJECT_TYPE_MAILBOX)): a spawned mailbox object in range, not any GUID.
/// </summary>
public sealed class MailboxAccessTests
{
    private const uint MailboxEntry = 176324;
    private const uint DoorEntry = 4000;
    private const uint NearSpawn = 91001;
    private const uint FarSpawn = 91002;
    private const uint DoorSpawn = 91003;

    [Fact]
    public async Task Only_a_spawned_mailbox_within_interaction_distance_is_usable()
    {
        var templates = new[]
        {
            new GameObjectTemplate { Entry = MailboxEntry, Type = (uint)GameObjectType.Mailbox, DisplayId = 3, Name = "Mailbox", Data = new uint[GameObjectTemplate.DataCount] },
            new GameObjectTemplate { Entry = DoorEntry, Type = (uint)GameObjectType.Door, DisplayId = 4, Name = "Door", Data = new uint[GameObjectTemplate.DataCount] },
        };
        // Human start is (-8949.95, -132.49, 83.53): 2 yd, 20 yd and 2 yd away.
        var spawns = new[]
        {
            new GameObjectSpawn { Guid = NearSpawn, Entry = MailboxEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f },
            new GameObjectSpawn { Guid = FarSpawn, Entry = MailboxEntry, MapId = 0, X = -8930f, Y = -132.5f, Z = 83.5f },
            new GameObjectSpawn { Guid = DoorSpawn, Entry = DoorEntry, MapId = 0, X = -8948f, Y = -134f, Z = 83.5f },
        };
        var context = new GameObjectTestContext(new GameObjectContent(templates, spawns, [], [], []), LootContent.Empty);
        GameObjectTestStore.Current.Value = context;
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("MAILBOXACC", "Mailboxer");
            var access = new DefaultMailboxAccess();
            ObjectGuid Guid(uint entry, uint spawn) => ObjectGuid.WithEntry(HighGuid.GameObject, entry, spawn);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Mailboxer")!;
                Assert.True(access.CanUseMailbox(player, Guid(MailboxEntry, NearSpawn)));
                Assert.False(access.CanUseMailbox(player, Guid(MailboxEntry, FarSpawn)));          // 20 yd: too far
                Assert.False(access.CanUseMailbox(player, Guid(DoorEntry, DoorSpawn)));            // a door, not a mailbox
                Assert.False(access.CanUseMailbox(player, Guid(MailboxEntry, 999999)));            // not spawned
                Assert.False(access.CanUseMailbox(player, ObjectGuid.Player(player.Guid.Low)));    // not a game object
                return true;
            });
        }
    }
}
