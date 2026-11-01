using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// A key-locked restocking chest keeps its lock. vmangos never clears GO_FLAG_LOCKED on unlock (GameObject.cpp:628-638 only sets GO_NOT_READY
/// and the cooldown), and EffectOpenLock (SpellEffects.cpp:2100-2175) checks the lock and takes the key on every open, so a reopen during the
/// restock countdown needs a key and uses it up again.
/// </summary>
public sealed class RestockChestKeyTests
{
    [Fact]
    public void SecondOpenDuringRestock_UsesUpAnotherKey()
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

        Assert.Equal(0u, player.Inventory.GetItemCount(ExpendableKey));
    }

    [Fact]
    public void SecondOpenDuringRestock_WithoutKey_IsRefused()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, KeyRestockChest, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(KeyRestockChest);
        ItemTestData.Give(player.Inventory, ExpendableKey);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        rig.LootOut(player, chest);
        rig.World.RunTick(50);
        Assert.Equal(0u, player.Inventory.GetItemCount(ExpendableKey));

        Assert.NotEqual(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
    }
}
