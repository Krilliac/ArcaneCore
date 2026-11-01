using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// A key-locked restocking chest is unlocked by its first open (vmangos GameObject::Update clears GO_FLAG_LOCKED, GameObject.cpp:629-636),
/// so opening it again during the restock countdown neither asks for nor uses up another key.
/// </summary>
public sealed class RestockChestKeyTests
{
    [Fact]
    public void SecondOpenDuringRestock_KeepsTheKey()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, KeyRestockChest, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(KeyRestockChest);
        ItemTestData.Give(player.Inventory, ExpendableKey, 2);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Equal(1u, player.Inventory.GetItemCount(ExpendableKey));
        rig.LootOut(player, chest);
        rig.World.RunTick(50);
        Assert.Equal(GameObjectLootState.NotReady, chest.LootState);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));

        Assert.Equal(1u, player.Inventory.GetItemCount(ExpendableKey));
    }

    [Fact]
    public void SecondOpenDuringRestock_NeedsNoKeyAtAll()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, KeyRestockChest, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(KeyRestockChest);
        ItemTestData.Give(player.Inventory, ExpendableKey);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        rig.LootOut(player, chest);
        rig.World.RunTick(50);
        Assert.Equal(0u, player.Inventory.GetItemCount(ExpendableKey));

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
    }
}
