using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The chest behaviours of the reference core: the chest level gate (Player::SendLoot, Player.cpp:7656-7665), a key from the bags used up by its
/// own spell charges (Spell::CanOpenLock + Spell::TakeCastItem), the restock timer (GameObject::Update, GameObject.cpp:381-394, 629-639) and the
/// multi-use mineral vein (WorldSession::DoLootRelease, LootHandler.cpp:435-487).
/// </summary>
public sealed class GameObjectChestTypeTests
{
    [Fact]
    public void ChestLevelGate_RefusesAnOpenerMoreThanTenLevelsBelow_WithALootRelease()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, LevelChest, 3, 0)]);
        (Player low, FakeSession lowSession) = rig.Join(1, level: 19);
        (Player high, FakeSession highSession) = rig.Join(2, level: 20);
        GameObject chest = rig.Single(LevelChest);
        lowSession.Clear();
        highSession.Clear();

        Assert.NotEqual(GameObjectUseResult.Ok, rig.System.Use(low, chest.Guid));
        List<(WorldOpcode Opcode, byte[] Payload)> sent = NonUpdatePackets(lowSession);
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgLootResponse);
        Assert.Equal(BitConverter.GetBytes(chest.Guid.Value).Concat(new byte[] { 1 }).ToArray(),
            Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgLootReleaseResponse).Payload);
        Assert.Null(chest.Loot);

        // chest.level 30 is exactly ten above 20: allowed.
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(high, chest.Guid));
        Assert.Single(Packets(highSession, WorldOpcode.SmsgLootResponse));
    }

    [Fact]
    public void KeyInTheBags_OpensTheChest_AndAnExpendableKeyIsUsedUp()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, KeyChest, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(KeyChest);

        Assert.Equal(GameObjectUseResult.MissingKey, rig.System.Use(player, chest.Guid));
        ItemTestData.Give(player.Inventory, ExpendableKey);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Equal(0u, player.Inventory.GetItemCount(ExpendableKey));
    }

    [Fact]
    public void KeyWithoutAnExpendableSpell_StaysInTheBags()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, DoorKeyChest, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(DoorKeyChest);
        ItemTestData.Give(player.Inventory, PlainKey);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Equal(1u, player.Inventory.GetItemCount(PlainKey));
    }

    [Fact]
    public void RestockChest_StaysInTheWorld_NotReadyUntilItsRestockTime_ThenGivesFreshLoot()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, RestockChest, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(RestockChest);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        rig.LootOut(player, chest);
        rig.World.RunTick(50);

        Assert.True(chest.IsSpawned);
        Assert.Equal(GameObjectLootState.NotReady, chest.LootState);
        Assert.Null(chest.Loot);
        session.Clear();
        Assert.NotEqual(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));

        rig.Seconds(29);
        Assert.Equal(GameObjectLootState.NotReady, chest.LootState);
        rig.Seconds(2);
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);
        Assert.True(chest.IsSpawned);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.NotNull(chest.Loot);
        Assert.False(chest.Loot!.IsEmpty);
    }

    [Fact]
    public void MineralVein_IsReadyAgainUntilItsMinimumOpens_ThenRolls_AndIsUsedUpAtItsMaximum()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, Vein, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject vein = rig.Single(Vein);
        rig.System.SkillValue = (_, _) => 300;
        rig.System.Random = new FixedRandom(0.0); // the next-open roll always passes

        // Open 1 (below minSuccessOpens 2): ready again, with fresh loot for the next open, and the skill-up list kept.
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, vein.Guid, LockType.Mining));
        vein.SkillupSet.Add(player.Guid);
        rig.LootOut(player, vein);
        rig.World.RunTick(50);
        Assert.True(vein.IsSpawned);
        Assert.Equal(GameObjectLootState.Ready, vein.LootState);
        Assert.Equal(GameObjectState.Ready, vein.State);
        Assert.Null(vein.Loot);
        Assert.Contains(player.Guid, vein.SkillupSet);

        // Open 2 (at the minimum, below the maximum 3): the roll decides; it passes here.
        session.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, vein.Guid, LockType.Mining));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse));
        rig.LootOut(player, vein);
        rig.World.RunTick(50);
        Assert.True(vein.IsSpawned);

        // Open 3 reaches maxSuccessOpens: used up, despawned.
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, vein.Guid, LockType.Mining));
        rig.LootOut(player, vein);
        rig.World.RunTick(50);
        Assert.False(vein.IsSpawned);
    }

    [Fact]
    public void MineralVein_AFailedNextOpenRoll_UsesItUp()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, Vein, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject vein = rig.Single(Vein);
        rig.System.SkillValue = (_, _) => 300;
        rig.System.Random = new FixedRandom(0.999);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, vein.Guid, LockType.Mining));
        rig.LootOut(player, vein);
        rig.World.RunTick(50);
        Assert.True(vein.IsSpawned);

        // uses 2: chance 100 * 0.8^(4/3*2) = 55.1 percent plus 300 / (175 + 25) = 1.5 for the skill; the roll 99.9 fails.
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, vein.Guid, LockType.Mining));
        rig.LootOut(player, vein);
        rig.World.RunTick(50);
        Assert.False(vein.IsSpawned);
    }
}
