using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectUseRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The use lifecycle of chests and goobers against vmangos: the chest lid state (Player::SendLoot :7699-7701, DoLootRelease
/// LootHandler.cpp:415-417), a partly looted chest staying activated and despawning five minutes later (LootHandler.cpp:503-511,
/// GameObject::Update GO_ACTIVATED :588-592), and the goober activation (GameObject::Use :1593-1606, Update :578-585, :606-623).
/// </summary>
public sealed class GameObjectUseLifecycleTests
{
    // --- the chest lid ---------------------------------------------------------------------------

    [Fact]
    public void Chest_OpensItsLidWhileTheWindowIsOpen_AndClosesItOnRelease()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, ChestEntry, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        Assert.Equal(GameObjectState.Ready, chest.State);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Equal(GameObjectState.Active, chest.State);

        rig.Loot.Release(player, chest.Guid);
        Assert.Equal(GameObjectState.Ready, chest.State);
    }

    // --- a partly looted chest -------------------------------------------------------------------

    [Fact]
    public void PartlyLootedChest_StaysActivated_SharesItsLoot_AndDespawnsFiveMinutesAfterRelease_ThenRespawnsFresh()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, ChestEntry, 3, 0, spawnTimeSeconds: 30)]);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        GameObject chest = rig.Single(ChestEntry);

        aliceSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        ParsedLoot window = ParsedLoot.Parse(Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootResponse)).Payload);
        Assert.Equal(Items.InventoryResult.Ok, rig.Loot.TakeItem(alice, window.Items[0].Slot));
        LootBag bag = chest.Loot!;
        rig.Loot.Release(alice, chest.Guid);
        Assert.Equal(GameObjectLootState.Activated, chest.LootState);

        // Another player still sees the leftovers (Player::SendLoot GO_ACTIVATED: FillNotNormalLootFor).
        bobSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, chest.Guid));
        Assert.Same(bag, chest.Loot);
        Assert.Single(ParsedLoot.Parse(Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootResponse)).Payload).Items);
        rig.Loot.Release(bob, chest.Guid);
        Assert.Equal(GameObjectLootState.Activated, chest.LootState);

        rig.Seconds(299);
        Assert.True(chest.IsSpawned);
        rig.Seconds(3);
        Assert.False(chest.IsSpawned);

        rig.Seconds(31);
        Assert.True(chest.IsSpawned);
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);
        Assert.Null(chest.Loot);
    }

    // --- goobers ---------------------------------------------------------------------------------

    [Fact]
    public void Goober_WithoutCustomAnim_SetsItsStateActive_SendsNoAnim_AndResetsAfterItsTimer()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, GooberEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        (_, FakeSession watcher) = rig.Join(2, 2, 0);
        GameObject goober = rig.Single(GooberEntry);
        session.Clear();
        watcher.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        Assert.Equal(GameObjectState.Active, goober.State);
        Assert.True(goober.Flags.HasFlag(GameObjectFlags.InUse));
        Assert.Equal(GameObjectLootState.Activated, goober.LootState);
        Assert.Empty(Packets(watcher, WorldOpcode.SmsgGameobjectCustomAnim));

        rig.Seconds(3);
        Assert.True(goober.IsSpawned); // GO_FLAG_NODESPAWN: not consumable, noDamageImmune clear
        Assert.Equal(GameObjectState.Ready, goober.State);
        Assert.False(goober.Flags.HasFlag(GameObjectFlags.InUse));
        Assert.Equal(GameObjectLootState.Ready, goober.LootState);
    }

    [Fact]
    public void Goober_WithACustomAnimFlagAndAnAutoClose_BroadcastsAnimZero_AndKeepsItsState()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, AnimGooberEntry, 3, 0)]);
        (Player player, _) = rig.Join(1);
        (_, FakeSession watcher) = rig.Join(2, 2, 0);
        GameObject goober = rig.Single(AnimGooberEntry);
        watcher.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        byte[] anim = Assert.Single(Packets(watcher, WorldOpcode.SmsgGameobjectCustomAnim)).Payload;
        Assert.Equal(goober.Guid.Value, BitConverter.ToUInt64(anim, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(anim, 8));
        Assert.Equal(GameObjectState.Ready, goober.State);
    }

    [Fact]
    public void ConsumableGoober_DespawnsOnlyWhenItsAutoCloseTimerEnds()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, ConsumableGooberEntry, 3, 0, spawnTimeSeconds: 60)]);
        (Player player, _) = rig.Join(1);
        GameObject goober = rig.Single(ConsumableGooberEntry);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        Assert.Equal(GameObjectState.Active, goober.State);
        rig.Seconds(2);
        Assert.True(goober.IsSpawned);
        rig.Seconds(3);
        Assert.False(goober.IsSpawned);
    }
}
